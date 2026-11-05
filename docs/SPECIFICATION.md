# MASTER PROMPT — .NET 8 / WPF Wave Flume SCADA-HMI & Wave-Generation System (6-Paddle Piston/Flap)

## 0. Ground rules for the AI coding agent (read first, obey throughout)

1. Build a **Windows desktop SCADA/HMI** in **C# 12 / .NET 8 / WPF (MVVM)** that upgrades the legacy **IITM WaveGen v1.1** (SAMUDRA / Indomer Coastal Hydraulics) for a **6-paddle segmented wavemaker**, talking to **any PLC brand over Ethernet**.
2. **Do not invent hydrodynamics.** Use only the formulas in Section 7. Anything marked **[VERIFY]** goes in its own method, has a unit test, and is listed in `docs/TODO_VERIFY.md` with the reference to check (Schäffer 1996; Madsen 1971; Hughes 1993; Dean & Dalrymple 1984; the IITM theory manual).
3. **No hardware is available during development.** Every PLC/DAQ interface must have a **Simulator** implementation (virtual flume) so the whole app runs and is tested on a laptop.
4. Every limit, address, IP, ratio and range lives in configuration (Section 3 + DB `system_settings`). **No magic numbers in code.** Internal units are **SI (m, s, m/s, m/s², N, W, rad)**; convert only at the UI and PLC boundaries.
5. The PC is a **supervisory, non-real-time** system (Windows is not deterministic). All hard safety and motion determinism live in the **PLC/drive**. Never claim "zero latency". The **hardwired E-stop chain is the primary stop**; the software E-stop is an additional command, never the only protection.
6. **Industrial look is mandatory** (Section 11.0). **Vendor-agnostic PLC connectivity with a configurable IP is mandatory** (Section 9).
7. Deliver in the phases of Section 15. After each phase the solution must build with 0 warnings treated as errors for the physics/PLC projects, pass its tests, and run.
8. Where the spec below is ambiguous or inconsistent, **use the resolved default in Section 2 and list the item in your phase report** — do not silently choose something else.

---

## 1. Role and scope

Act as a Principal Industrial .NET Architect and Coastal Hydrodynamics Control Engineer. The application lets an operator: define a wave condition → compute and **safety-validate** the 6-paddle motion → send it to the PLC → watch paddle position and wave-probe signals live → log data → compute wave statistics → export a PDF report. Recipes, runs, alerts and settings are stored in MySQL; high-rate telemetry goes to CSV only.

**Non-goals:** real-time servo control on the PC; directional (spread) seas in v1; reflection-compensation (active absorption) in v1.

---

## 2. Resolved conflicts and defaults (the draft spec contradicted itself — these decisions apply)

| Topic | Conflict in earlier drafts | Decision |
|---|---|---|
| Stroke limit | 700 mm total vs ±450 mm vs ±500 mm hard | Hardware travel ±500 mm (informational). **Absolute software ceiling ±450 mm** (cannot be raised from UI). **Default operational clamp ±350 mm (= 700 mm total, as IITM Settings)**, admin-editable up to the ceiling. All "max stroke" checks use the operational clamp. |
| Period | range 0.5–6 s vs `T_min` 0.6 s vs gate 0.30 s | Absolute floor **0.30 s** (hard-coded). Operational `T_min` default **0.6 s**, `T_max` 6.0 s. **Effective minimum = max(range_min, T_min, 0.30)**; UI shows the effective value. Lowering below 0.6 s is only allowed if the velocity/acceleration check passes. |
| Water depth | flume 0.8 m deep but range up to 1.0 m | Add `max_water_depth_m` (default **0.8**, physical). Depth input range = `[0.2, min(1.0, max_water_depth_m)]`. Ask the engineer to confirm. |
| Max acceleration | draft default 5000 mm/s² (arbitrary) | **No default.** `max_accel_mms2` is NULL until an admin sets it; **Start/Send is disabled while NULL** and the status bar says "Acceleration limit not configured". |
| Velocity | 1.57 m/s | Keep `1.57 m/s` as the absolute ceiling and default clamp. |
| Number of tables | original 3 vs draft 4 | **4 tables** (`system_settings`, `wave_recipes`, `test_runs`, `telemetry_alerts`); no operator/user table. |
| "Zero-latency" | marketing wording | Removed. Target: UI ≥ 30 FPS (goal 60), telemetry timestamps jitter-logged. |
| HBM/NI DAQ | legacy IITM used HBM/NI | Default probe path = **PLC analog inputs** (`PlcAnalogDaqSource`). `IDaqSource` stays pluggable; an HBM adapter is a stub until a protocol is specified. |
| "Start EWT" | legacy label | Rename **Start Wave**. |
| Command vs telemetry rate | one "sampling frequency" | Two settings: `command_fs_hz` (paddle trajectory, 50–200, default **125**) and `telemetry_rate_hz` (default = `command_fs_hz`). |

---

## 3. Physical system and configuration (`flume_config.json` + `system_settings`)

| Item | Value |
|---|---|
| Flume | 3.0 m wide × 45.0 m long; water depth `h` per §2 |
| Paddles | 6 independent actuators, width `b = 0.5 m`, centres `y_i = 0.25, 0.75, 1.25, 1.75, 2.25, 2.75 m`; type **Piston** or **Flap/Elevated hinge** (`h0`) |
| Drive | Servo → 20:1 gearbox → Module-4, 25-tooth pinion (PCD 100 mm → **314.159 mm per pinion rev**, 15.708 mm per motor rev) |
| Wave height range | 0.01–0.30 m |
| Period range | per §2 |
| Number of waves | 10–10 000 |
| `command_fs_hz` | 50–200 Hz (default 125) |
| Controller | Any PLC over wired Ethernet; default profile Omron NX/NJ `192.168.250.1:44818` |
| DAQ/probes | up to 8 analog probes (4–20 mA) via PLC; optional external DAQ IP (legacy HBM `192.168.169.5`) |

`Units.MmToMotorCounts(mm, encoderCountsPerRev)` with round-trip tests (encoder resolution from config).

---

## 4. Tech stack and solution structure

- **.NET 8, C# 12, `net8.0-windows`**, WPF, **MVVM with CommunityToolkit.Mvvm**, `Microsoft.Extensions.DependencyInjection/Hosting`, **Serilog** (rolling files).
- **Plots:** **ScottPlot.WPF 5.x** (`WpfPlot`; preallocated arrays, `DataStreamer`/`Signal`, redraw on a 16 ms `DispatcherTimer` only when new data exists).
- **Math:** **MathNet.Numerics** (`Fourier.Inverse`/FFT, interpolation, integration). Root-finding is hand-written Newton–Raphson.
- **PLC/Ethernet:** `libplctag` (.NET wrapper; EtherNet/IP CIP, PLC type `omron-njnx` for NX/NJ, `controllogix` for Rockwell), **S7netplus** (Siemens; requires PUT/GET enabled), **NModbus** (Modbus TCP), **OPCFoundation.NetStandard.Opc.Ua.Client** (OPC UA), own FINS/UDP-TCP client, own Mitsubishi SLMP client (Mitsubishi is **not** Modbus-native).
- **DB:** **MySqlConnector** (+ Dapper optional), async, parameterised only. **CSV:** `Channel<T>` producer–consumer + `StreamWriter`. **PDF:** **QuestPDF** (set `QuestPDF.Settings.License = LicenseType.Community;` and note the licence terms in README). **Secrets:** `System.Security.Cryptography.ProtectedData` (DPAPI). **Tests:** xUnit + FluentAssertions.
- **No third-party UI kits** (no MaterialDesign, ModernWpf, MahApps, HandyControl, Fluent themes).

```
WaveFlume.sln
├─ src/
│  ├─ WaveFlume.Core/         records, enums, Units, config models, SafetyReport, state machine
│  ├─ WaveFlume.Physics/      WaveMathEngine (Dispersion, TransferFunctions, SecondOrder, Spectra,
│  │                          RandomWaveSynth, OrientationPhase, Ramp, TrajectoryGenerator,
│  │                          SafetyGatekeeper, LimitCurve, WaveStatistics)
│  ├─ WaveFlume.Plc/          IPlcDriver, DriverFactory, TagMap, Modbus/OpcUa/OmronFins/EtherNetIp/
│  │                          S7/Slmp/Ads drivers, SimulatorDriver, PlcTelemetryService
│  ├─ WaveFlume.Data/         DatabaseService, repositories, CsvLogger, ProbeCalibration
│  ├─ WaveFlume.Reporting/    PdfReportService
│  └─ WaveFlume.App/          WPF Views, ViewModels, Themes (Light.xaml, Dark.xaml), Controls
├─ tests/                     WaveFlume.Physics.Tests, .Plc.Tests, .Data.Tests
├─ sql/schema.sql
├─ config/flume_config.json
└─ docs/ TODO_VERIFY.md  PLC_IO_MAP.md  UI_STYLE_GUIDE.md  README.md
```

---

## 5. Architecture, threading, data flow

`View ↔ ViewModel` (CommunityToolkit `ObservableObject`, `RelayCommand`/`AsyncRelayCommand`, messenger for cross-VM events).
Flow: UI inputs → `WaveMathEngine` → `SafetyGatekeeper` → `TrajectoryGenerator` → `IPlcDriver` → PLC. PLC → `PlcTelemetryService` → (a) ring buffers for plots, (b) `Channel<TelemetrySample>` → `CsvLogger`, (c) rolling statistics.

- **Never block the UI thread.** All I/O `async`; heavy generation in `Task.Run`; progress via `IProgress<T>`; every async call takes a `CancellationToken`.
- **Timing:** the telemetry loop runs on a **dedicated thread** (not `Task.Delay`), using `Stopwatch` and `timeBeginPeriod(1)` (winmm) — `PeriodicTimer` alone cannot hold 200 Hz. Stamp every sample with a monotonic timestamp; log jitter and missed cycles.
- **Preferred telemetry mode (block mode):** the PLC samples paddles/probes at `telemetry_rate_hz` into a ring buffer with a **sample counter**; the PC reads blocks at 20–50 Hz and rebuilds the time axis from the counter. This removes PC timing jitter and detects lost samples. **Fallback (poll mode):** single-sample polling at 50–200 Hz. Both are drivers' capabilities.
- Logging backpressure: bounded `Channel<T>` (`BoundedChannelFullMode.Wait` for CSV; drop-oldest for UI buffers); flush every 1 s and on stop.
- Application state machine (also enables/disables buttons): `Disconnected → Connected → ServosEnabled → Homed → TrajectoryLoaded → Running → Stopping → (Faulted)`.

---

## 6. Wave Math Engine API (`WaveFlume.Physics`)

Pure, UI-free, deterministic (seeded), fully unit-tested. `WaveMathEngine.Calculate(WaveRequest) → WaveResult` returning kinematics, derived outputs, `Trajectory` (6 × N float32 at `command_fs_hz`), `SafetyReport`. Use `double` for calculation, `float` for stored trajectories.

---

## 7. Hydrodynamic formulations (use exactly these)

Notation: `g = 9.81`, `ω = 2π/T`, `A_w = H/2`, `h = d` water depth, `S0` full stroke, `X_a` paddle amplitude.

### 7.1 Dispersion
`ω² = g k tanh(kh)` by Newton–Raphson: initial guess `k0 = ω²/(g·√tanh(ω²h/g))`; `f(k)=gk·tanh(kh)−ω²`; `f'(k)=g·tanh(kh)+gkh·sech²(kh)`; ≤ 15 iterations, tolerance 1e-12, throw on non-convergence. `L = 2π/k`, `C = L/T`, `n = ½(1+2kh/sinh 2kh)`, `Cg = nC`.

### 7.2 Biesel transfer functions (`c0 = H/S0`)
- **Piston:** `c0 = 4 sinh²(kh) / (sinh(2kh) + 2kh)` (= `2 sinh²(kh)/(sinh kh cosh kh + kh)`).
- **Elevated hinge** (hinge height `h0`; `S(z)=S0·(h+z−h0)/(h−h0)` for `(z+h)>h0`, else 0):
  `c0 = 2·sinh(kh)·[(h−h0)·k·sinh(kh) − cosh(kh) + cosh(k·h0)] / ( k(h−h0)·[sinh(kh)cosh(kh) + kh] )`
- **Flap (hinged at bottom)** = elevated hinge with `h0 = 0` (unit test: equal to `4 sinh(kh)(kh sinh kh − cosh kh + 1)/(kh(sinh 2kh + 2kh))`).

### 7.3 First-order paddle motion (manual eq. 1)
`X0⁽¹⁾(t) = Σ_j ½{ −i X_aj e^{iω_j t} + c.c. } = Σ_j X_aj sin(ω_j t)`, `X_aj = A_j / c0j`. Single component: **`X_a = H/(2·c0)`** (this is the half-stroke; display it as "Stroke amplitude", and also "Stroke peak-to-peak" = `2X_a`).

### 7.4 Second-order theory (manual eq. 2–3)
Bound waves: `η⁽²¹⁾± = { G±_nm · A_n · A_m^{(±)} · e^{i(θ_n ± θ_m)} + c.c. }`, `θ_j = k_j x − ω_j t`, with `A_m^{(+)} = A_m` (super-harmonics) and `A_m^{(−)} = A_m*` (sub-harmonics).
Paddle: `X0⁽²⁾± = ½{ −i F± · A_n · A_m^{(±)} / d · e^{i(ω_n ± ω_m)t} + c.c. }`.
`F±` (includes the compensating free wave that cancels the spurious free wave) differs from `G±` (bound wave only). Four potentials: first-order, bound-wave interaction, spurious free wave, compensating free wave.
- **[VERIFY]** Implement `F±_nm` and `G±_nm` from **Schäffer (1996), "Second-order wavemaker theory for irregular waves", Ocean Eng. 23(1)** (the manual uses its asymptotic summation). If the closed forms are unavailable, throw `NotImplementedException` with a clear UI message and keep the 1st-order path working. Never approximate from memory.
- **[VERIFY] Monochromatic 2nd-order (Madsen 1971, piston):** `X0⁽²⁾(t) = (H²/(32h))·(3cosh(kh)/sinh³(kh) − 2/c0)·sin(2ωt)`; check sign/phase against eq. 3 with `n=m` (`X0⁽²⁾ = (F⁺A²/d)·sin 2ωt` for real `F⁺`).
- Validation target in the simulator: generated wave contains the Stokes-2nd bound term `η₂ = (H²k/16)·cosh(kh)(2+cosh 2kh)/sinh³(kh)·cos 2θ` and **no free second harmonic**.
- Applicability warnings: Ursell `Ur = H L²/h³ > ~26` or `kh < 0.3` (use cnoidal/solitary instead).

### 7.5 Wave types → paddle trajectory (sample rate `command_fs_hz`)

| Type | Target | Method |
|---|---|---|
| **Monochromatic** | `η = a cos(kx − ωt)` | `X = (H/2c0)·sin(ωt)` for `N` waves (+ 2nd-order term when selected) |
| **Bichromatic** | `η = Σ_{i=1,2} a_i cos(k_i x − ω_i t)` | inputs `H1,T1,H2,T2`; sum of first-order components with each `c0(k_i h)`; 2nd order adds sum/difference terms (eq. 3) |
| **Random** | spectrum `S(f)` | §7.6 |
| **Focusing** | phase-focused group | `φ_i = −k_i x_f + ω_i t_f`; inputs focus position `x_f`, time `t_f`, amplitude, band |
| **Solitary** | `η = H sech²(κ(x−ct))`, `κ=√(3H/4h³)`, `c=√(g(h+H))` | Goring (1978): `dX/dt = c·η(X,t)/(h+η(X,t))` integrated numerically (RK4); report total stroke |
| **Cnoidal** | cnoidal theory | Phase 4 (Jacobi elliptic functions); UI entry present, returns "not yet implemented" until then |
| **User-defined waves** | CSV η(t) | FFT → divide by `c0(k(f)h)` → inverse FFT |
| **User-defined stroke** | CSV X(t) | used directly, still safety-checked |

`% Signal Modification` multiplies the final paddle signal (> 100 % requires a confirmation dialog).

### 7.6 Random waves (manual flowchart; both paths)
**Path A (paddle positions):** define `S(f)` → convert to paddle spectrum with `1/c0(f)` → inverse FFT with **seeded random phases** → paddle time series `x(t)`. **Path B (surface):** `S(f)` → inverse FFT (random phases) → η(t) → transfer function → `x(t)`.
`x(t) = Σ_i [a_i cos ω_i t + b_i sin ω_i t]`, `ω_i = 2π iΔf`, amplitude `A_i = √(2 S(f_i) Δf)` with phase `ε_i ~ U(0,2π)` from `System.Random(seed)` (repeatable; store the seed).
- **Duration & resolution:** `D = N_waves · T_p` (or user-supplied), `Δf = 1/D`; record length is the repeat period (state it in the UI).
- **Band-limit (mandatory):** because `c0 → 0` at low frequency the paddle amplitude `A/c0` explodes. Use components only in `[f_lo, f_hi]`, `f_lo = max(1/T_max, 0.4 f_p)`, `f_hi = min(1/T_min_eff, 4 f_p)` (configurable); report the % of energy removed.
- **Hs normalisation:** every spectrum is generated as a *shape* and then scaled so that `m0 = ∫S df = Hs²/16` (numerical trapezoid on a fine grid). The realised `Hs` of one seed will scatter ±~10 %: offer **"scale to target Hs"** (rescale the realisation) as an option.
- **Memory:** 10 000 waves × 6 s × 125 Hz ≈ 7.5 M samples/paddle. Synthesise **one paddle at a time**, store as `float32`, warn above a configurable size, and upload in chunks.

### 7.7 Spectral shapes (corrected normalisation — `fp = 1/Tp`)
- **Pierson–Moskowitz / Bretschneider:** `S(f) = (5/16) Hs² fp⁴ f⁻⁵ exp[−(5/4)(f/fp)⁻⁴]`.
- **Generalised PM (parameter β, default 1.25):** `S(f) = (β/4) Hs² fp⁴ f⁻⁵ exp[−β (f/fp)⁻⁴]`. *(The coefficient is `β/4`, not the fixed `5/16`, otherwise `Hs` is wrong for β ≠ 1.25; at β = 1.25 it equals the PM above. β > 1.25 narrower, β < 1.25 wider.)*
- **JONSWAP (shape):** `S ∝ f⁻⁵ exp[−(5/4)(f/fp)⁻⁴] · γ^{ exp[−½((f−fp)/(σ fp))²] }`, `γ = 3.3` default, `σ = 0.07` (`f ≤ fp`) / `0.09` (`f > fp`); reduces to PM when `γ = 1`. The Phillips constant `α` is **not** an input — it is found by the `m0 = Hs²/16` normalisation.
- **TMA (depth-limited):** `S_TMA = φ(ω_h)·S_JONSWAP`, `ω_h = 2πf√(h/g)`; `φ = 0.5ω_h²` (`ω_h ≤ 1`), `1 − 0.5(2−ω_h)²` (`1 < ω_h ≤ 2`), `1` (`ω_h > 2`); then renormalise to `Hs`.
- **Darbyshire — [VERIFY]:** draft formula `S = 1.429·Hs²·exp[−( (f−fp)² / (0.0047 (f−fp+0.042)) )^{1/2}]` for `f − fp > −0.042`, 0 otherwise. As written it is dimensionally suspect; implement behind a flag, compare with the IITM manual's spectrum page, renormalise to `Hs`, and do not release until verified.

### 7.8 Oblique / 3D (6-paddle segmented)
Per paddle `p` (centre `y_p`) and component `i`: `φ_{p,i} = −k_i · y_p · sin θ`, i.e. `X_p(t) = Σ_i X_ai sin(ω_i t + φ_{p,i})` (apply per component in the frequency domain for random waves). Guards: adjacent-paddle phase step `Δφ = k·b·sinθ` must be `< π/2` (warning) and `< π` (hard reject); warn that oblique waves in a 3 m wide flume reflect from side walls unless absorbers exist. Optional **[VERIFY]** Dalrymple (1989) oblique transfer-function correction behind a config flag. Directional spreading is out of scope for v1.

### 7.9 Ramp
`R(t) = ½(1 − cos(π t/T_ramp))` for `0 ≤ t ≤ T_ramp`, then 1; mirrored ramp-down for **Smooth Stop**; guarantee `X(0)=0` and continuous velocity at start/end.

### 7.10 Derived outputs (Wavemaker/Kinematics panels)
- Stroke amplitude, peak-to-peak, `V_peak = X_a ω`, `A_peak = X_a ω²` (second-order terms included for 2nd order), `L`, `C`, `k`, `kh`, regime (deep/intermediate/shallow), `u_max = πH/T` (matches the legacy display; exact orbital surface velocity `×coth(kh)` as tooltip).
- **Wave power** delivered: `P = (ρ g H²/8)·Cg·W` (W = 3.0 m flume width; also per paddle with `b = 0.5 m`). `ρ = 1000 kg/m³` (config for salt water).
- **Peak hydrodynamic force — documented estimate [VERIFY]:** linear-theory dynamic force amplitude on a paddle face of width `b`: `F = ρ g A_w · tanh(kh)/k · b` (wave-induced part only; excludes hydrostatic and evanescent-mode terms). Label it "estimate" in the UI. The legacy IITM kN value has no published formula, so do not try to match it.

### 7.11 Operating-limit curve (Limit tab)
For `T ∈ [T_min_eff, T_max]`: `H_lim(T) = min(H_break, H_cap, H_stroke, H_vel, H_acc)` where `H_break = 0.142·L·tanh(kh)` (Miche; rising left part), `H_cap` = configured max height (plateau), `H_stroke = 2·c0·X_max`, `H_vel = 2·c0·V_max/ω`, `H_acc = 2·c0·a_max/ω²` (decaying right part). The current operating point is a marker (muted green inside / red outside); hover shows the active limit; **Calculate/Send is blocked while outside.**

### 7.12 Zero-upcrossing statistics (`WaveStatistics`)
Per probe: remove mean, discard ramp segments (configurable), split on zero up-crossings, wave height = max − min within a wave, period = up-crossing interval. Outputs: `H1/3` (mean of highest ⅓), `H10` (mean of highest 1/10), `Hmax`, `Hrms`, `T1/3`, `T10`, `Tmax`, `Tz`; spectral `m0`, `Hm0 = 4√m0`, `Tp` (Welch, Hann). Live version on a sliding window (updated 1 Hz), final version at run end.

---

## 8. Safety gatekeeper (`SafetyGatekeeper`)

Run **before anything is sent**, on the **full generated trajectory** (all six paddles, second-order terms and % modification included), not only on the sinusoid formula:
- `max|X_p| ≤ clamp` (≤ 0.45 m ceiling); `max|dX/dt| ≤ V_max` (≤ 1.57 m/s); `max|d²X/dt²| ≤ a_max` (must be configured); `T ≥ effective T_min ≥ 0.30 s`.
- Inputs inside Settings ranges; `command_fs_hz ≥ 10 × f_max`; phase-step guard (§7.8); trajectory has `X(0)=0`.
- Returns `SafetyReport { Ok, Violations[], WorstValues }`; the UI shows the first violation in red; every violation is written to `telemetry_alerts`. **The PLC must also enforce the same limits** (defence in depth) — state this in `docs/PLC_IO_MAP.md`.
- Runtime guard: telemetry paddle position > 95 % of clamp, following error above threshold, probe fault (< 3.8 mA or > 20.5 mA), or heartbeat loss → raise `Critical` alert and command **Smooth Stop** (E-stop if the PLC reports a hard fault).

---

## 9. Universal PLC Ethernet layer (`WaveFlume.Plc`)

### 9.1 Vendor-agnostic design
The application only knows **logical signals**: `CmdWord, Period_s, Ramp_s, Cycles, Amp_mm[1..6], Phase_deg[1..6], Pos_mm[1..6], Probe_m[1..8], StatusWord, FaultWord, Heartbeat, SampleCounter, TrajChunk, ChunkSeq, ChunkReady, ChunkAck, BufferLevel`. A **TagMap** (editable table, saved as JSON profile) maps each to a protocol address, data type, scale, offset and access. Changing the PLC brand = pick another driver + load another tag map; **no recompilation**.

```csharp
public interface IPlcDriver : IAsyncDisposable
{
    PlcProtocolType Protocol { get; }
    DriverCapabilities Capabilities { get; }          // MaxBlockWords, SupportsFloat32, SupportsBlockTelemetry
    event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

    Task<ConnectResult> ConnectAsync(PlcEndpoint endpoint, CancellationToken ct);   // ip, port, protocol extras
    Task DisconnectAsync();
    Task<PingResult> PingAsync(CancellationToken ct);                               // protocol handshake + RTT
    Task<bool> SendWaveRecipeAsync(WaveCommandPacket packet, CancellationToken ct); // parametric mode
    Task<bool> UploadTrajectoryChunkAsync(TrajectoryChunk chunk, CancellationToken ct); // trajectory mode
    Task<bool> SendControlCommandAsync(PlcControlCommand cmd, CancellationToken ct);// Enable, Home, Start, SmoothStop, EStop, Reset
    Task<TelemetrySnapshot> PollTelemetryAsync(CancellationToken ct);               // poll mode
    Task<TelemetryBlock> ReadTelemetryBlockAsync(CancellationToken ct);             // block mode (counter-based)
}
```

| Protocol (Ethernet) | Typical PLCs | Default port | Address style in TagMap | Library | Release |
|---|---|---|---|---|---|
| **Modbus TCP** (universal) | Schneider, Delta, WAGO, many Omron/Siemens with Modbus enabled, drives | 502 | holding register `HR100`, unit ID | NModbus | **1** |
| **OPC UA** (universal, modern) | Siemens S7-1500, Beckhoff, Omron NX/NJ, B&R, Rockwell/Mitsubishi with server | 4840 | `ns=3;s="DB_Wave"."Period"` | OPCFoundation client | **1** |
| **Omron FINS UDP/TCP** | Omron CJ/CP/NX/NJ (NX/NJ only via variables mapped to fixed DM addresses) | 9600 | `D100` | own client | **1** |
| **EtherNet/IP CIP** | Omron NX/NJ, Rockwell Allen-Bradley | 44818 | tag name `Wave.Period` | libplctag | 2 |
| **Siemens S7comm** | S7-1200/1500 (PUT/GET), 300/400 | 102 | `DB10.DBD4`, rack/slot | S7netplus | 2 |
| **Mitsubishi SLMP/MC** | iQ-R, iQ-F, Q/L | configurable | `D100` | own client | 3 |
| **Beckhoff ADS** | TwinCAT | 48898 | symbol, AMS NetID | Beckhoff.TwinCAT.Ads | 3 |
| **Simulator** | none (virtual flume) | n/a | built-in | — | **always** |

`DriverFactory.Create(PlcProtocolType)` builds drivers.

### 9.2 Two motion-transfer modes
1. **Parametric mode** (monochromatic/oblique): one packet — Command Word, Period `T`, Ramp time, cycles, 6× amplitude (mm), 6× phase (deg). The PLC generates the sine.
2. **Trajectory mode** (default for all other types, optional for monochromatic): upload `6 × N` position samples at `command_fs_hz` into a PLC **ring buffer in chunks** (size ≤ `MaxBlockWords`, e.g. Modbus ≤ 123 registers/write) with `ChunkSeq`, checksum, `ChunkReady/ChunkAck`, `BufferLevel`; the PLC interpolates to the servo cycle. Generate the full memory map into `docs/PLC_IO_MAP.md` from the TagMap.

Command-word bits: Enable, Home, Start, SmoothStop, **EStop (additional to hardwired chain)**, Reset, Mode(Param/Traj). Default telemetry registers (configurable): paddle positions `D100–D110`, probes `D200–D208`, status/fault after that, then `SampleCounter`, `Heartbeat`. Float words use configurable byte/word order.

### 9.3 Omron FINS framing (own client; unit-test with captured byte arrays)
Header (10 bytes) `ICF=0x80, RSV=0x00, GCT=0x02, DNA, DA1, DA2, SNA, SA1, SA2, SID`, then `0x0101` memory read / `0x0102` write; DM area code `0x82`; 3-byte address; 2-byte count; data big-endian; check end code `0x0000`; match `SID`; timeout 100 ms, 3 retries then fault.

### 9.4 Connection robustness
Heartbeat word toggled every cycle (both directions); loss > configured timeout → fault + Smooth Stop. Auto-reconnect with exponential back-off. Per-driver communication-error counter and last-error text exposed to the UI.

### 9.5 Controller Connection panel (Settings tab)
- **Protocol** dropdown (§9.1), **IP address** (validated IPv4), **Port**, **Transport** (UDP/TCP where applicable), connect/response timeout, retries, polling/telemetry rate, protocol extras shown only when relevant (Modbus unit ID, S7 rack/slot, FINS node/network numbers, ADS AMS NetID, OPC UA security mode and credentials), byte/word order.
- **Test Connection** button: protocol handshake (not ICMP only), reports exact reason (timeout / refused / wrong node / access denied), RTT in ms, reads one test tag.
- **Connection status indicator** in the status bar on every tab: Disconnected / Connecting / Connected (RTT) / Fault, with error counter.
- **Network hint:** list the PC's IPv4 addresses and warn when the PLC IP is not on a PC subnet (e.g. PC `192.168.1.x` vs PLC `192.168.250.1`); remind about a static IP on the flume NIC and Windows Firewall rules for the chosen port.
- **TagMap editor** (DataGrid): *Logical signal · Address · Type (BOOL/UINT16/INT32/FLOAT32) · Scale · Offset · R/W · Units*; Import/Export JSON; **Validate all tags** button; named **profiles** ("Omron NX102 – lab", "Siemens S7-1500", "Simulator").
- Credentials are DPAPI-encrypted; editing needs admin login; **Connect is disabled while the TagMap fails validation.** `IDaqSource` selection (PLC analog / Simulator / external DAQ IP) lives here too.

---

## 10. DAQ, calibration, logging

- **ProbeCalibration** ("Channels and Calibrations"): per-channel linear `η = gain·raw + offset`; wizard — dip each probe at ≥ 3 known depths, least-squares fit, show R² and residuals, persist. 4–20 mA fault detection (§8).
- **CsvLogger:** one file per run `Runs\yyyyMMdd_HHmmss_<testNumber>.csv`, columns `t_s, paddle1..6_mm, probe1..N_m, status_word, sample_counter`; must sustain 200 Hz × 20 columns indefinitely; "Extra duration to record (s)" continues logging after the wave command ends; header includes recipe, units and calibration snapshot.
- Optional later: Goda–Suzuki 2/3-probe reflection analysis.

---

## 11. WPF UI

### 11.0 Visual design — industrial HMI, not a "modern app" (ISA-101 principles)
Target look: IITM WaveGen / WinCC / Wonderware / Sysmac Studio. Plain, dense, functional.

**Do**
- Built-in WPF controls restyled in `Themes/Light.xaml` and `Themes/Dark.xaml` (swap via `DynamicResource`). Default **light-gray** (window `#D4D4D4`–`#E6E6E6`, panels `#EDEDED`, text `#1A1A1A`); **dark-gray** option (`#2B2B2B`/`#3A3A3A`, text `#E0E0E0`). Both fully legible.
- Flat, square, bordered: 1 px solid gray borders, 0–2 px radius, titled `GroupBox` frames, standard buttons with clear pressed/disabled states, `DataGrid` with gridlines. `UseLayoutRounding="True"`, `SnapsToDevicePixels="True"`, `TextOptions.TextFormattingMode="Display"`.
- **Colour only means something:** gray = normal; red = alarm/E-stop/fault; amber = warning; muted green only for running/OK lamps; blue only for selection. Every alarm colour has a text/shape cue (colour-blind safe).
- Fixed 8 px grid, aligned labels, **units always shown**, right-aligned numeric fields, Consolas for live numbers, Segoe UI 9–10 pt otherwise. Full keyboard navigation (tab order, access keys, Enter applies). Only Start / Stop / E-Stop are large (≥ 48 px).
- Permanent **status bar:** controller (protocol, IP, RTT), servo state, run state, active-alarm count, DB connection, mode (Operator/Admin), clock. **Alarm banner** under the menu showing the highest-priority active alarm with Acknowledge, plus an alarm/event log table (ISA-18.2 style: priority, time, ack state).
- Plots: plain white or near-black background, 1 px solid traces in a fixed standard palette, labelled axes with units, light grid, no smoothing effects; classic black/yellow-trace oscilloscope option.
- Confirmation dialogs for dangerous actions (Start with high amplitude, % modification > 100, leaving a run in progress). E-STOP: solid red, white text, always visible, never animated.

**Do not:** gradients, drop shadows, glass/blur/glow, navy/purple "tech" palettes, neon accents, rounded floating cards, pill buttons, hero banners, decorative icons or emojis, animated transitions, web-style sidebars, friendly chatbot wording. Only a small college/project logo in the header/About. Labels terse and technical ("Water Depth (m)").

**Deliverables for this section:** `Themes/Light.xaml`, `Themes/Dark.xaml`, `docs/UI_STYLE_GUIDE.md` (exact colours, fonts, spacing, control states) and a screenshot of each tab in both themes.

### 11.1 Main window
`MainWindow.xaml` with top menu/tabs: **Wave Generation | Wave Paddle | Settings | Help** (manual PDF, About, licences).

### 11.2 Tab 1 — Wave Generation
- Left: **Wave Order** (1st / 2nd); **Wave Type** (Monochromatic, Bichromatic, Random, Cnoidal, Solitary, Focusing, User defined waves, User defined stroke). Conditional panels: Random → spectral shape (PM / Generalised PM β / JONSWAP γ / TMA / Darbyshire), `Hs`, `Tp`, seed, "scale to target Hs"; Bichromatic → `H2, T2`; Focusing → `x_f, t_f`; Oblique θ available for Monochromatic/Bichromatic/Random.
- **Wave Parameters:** Water Depth (m), Signal Wave Height (m), Peak Wave Period (s), Number of Waves, Sampling Frequency (Hz), % Signal Modification, Ramp Duration (s), Wave Angle θ (deg).
- Buttons **Calculate**, **Reset**. **Recipe block:** Test Number, Save, Add, Update, Load Prev Wavegen, New → MySQL (offline fallback to local JSON if the DB is unreachable).
- Read-only **Wavemaker Parameters:** Stroke (m), Velocity (m/s), Acceleration (m/s²), Force (kN, estimate), Power (W). **Wave Kinematics:** Wave Length, Celerity, Max Velocity u.
- Bottom-right plot tabs (ScottPlot): **Limit | Wave Board** (planned 6-paddle displacement preview) **| Wave Elevation** (predicted η(t)) **| Spectrum** (target, later measured), with Zoom.

### 11.3 Tab 2 — Wave Paddle
- Left: run selector (Open), Extra duration to record (s); toggles **PLC Ethernet connection** and **DAQ/Probe stream** (+ Assistant diagnostics); buttons **Connect · Enable Servos · Home · Send to Wave Paddle · Start Wave · Smooth Stop · EMERGENCY STOP · Save Test Data**, state-gated by the state machine (§5); **Statistical Parameters** (H1/3, H10, Hmax, T1/3, T10, Tmax; live and final).
- **Six live paddle indicators** (bar + mm; colour change at 80 % / 95 % of the clamp).
- Right: tabs **Real Time Board Motion** (6 paddle traces) **| Plot Wave Probe Data**; toolbar Open Test Data and Show (Time Series / Spectrum); **3 stacked synchronised oscilloscope charts**, each with a Channel No. selector; window length (10/30/60 s), pause/freeze; redraw ≥ 30 FPS (goal 60) from ring buffers decimated to ≤ 2 points per pixel.

### 11.4 Tab 3 — Settings
- Type of Paddle (Piston / Flap with hinge height `h0`); Max Stroke, Max Velocity, Max Acceleration, Width of Wave Paddle (0.5 m × 6), Location of Wave Board, `T_min`, `T_max`, Calibration factor, Path settings, Control selector (PLC / External DAQ), theme, units display.
- **Admin Access:** Login to Edit / Save Settings (hashed password; the hard ceilings of §2 cannot be raised even by admin).
- **Range Bounds:** water depth, wave height, wave period, number of waves, sampling frequency (min–max).
- **Controller Connection** panel (§9.5), MySQL connection (host, port, db, user, DPAPI-protected password; stored locally — **not** in the DB), test-connection button.

---

## 12. MySQL schema (`sql/schema.sql`) and `DatabaseService`

```sql
CREATE DATABASE IF NOT EXISTS wave_flume_scada CHARACTER SET utf8mb4;
USE wave_flume_scada;

CREATE TABLE system_settings (
  setting_id            INT PRIMARY KEY DEFAULT 1,
  paddle_type           ENUM('Piston','Flap') NOT NULL DEFAULT 'Piston',
  hinge_height_m        FLOAT NOT NULL DEFAULT 0.0,
  max_stroke_mm         FLOAT NOT NULL DEFAULT 350.0 COMMENT 'operational +/- clamp; ceiling 450 enforced in code',
  max_velocity_mms      FLOAT NOT NULL DEFAULT 1570.0,
  max_accel_mms2        FLOAT NULL COMMENT 'NULL = not configured; Start disabled',
  paddle_width_m        FLOAT NOT NULL DEFAULT 0.5,
  num_paddles           INT   NOT NULL DEFAULT 6,
  max_water_depth_m     FLOAT NOT NULL DEFAULT 0.8,
  t_min_s               FLOAT NOT NULL DEFAULT 0.6,
  t_max_s               FLOAT NOT NULL DEFAULT 6.0,
  command_fs_hz         FLOAT NOT NULL DEFAULT 125.0,
  telemetry_rate_hz     FLOAT NOT NULL DEFAULT 125.0,
  plc_protocol          VARCHAR(50) NOT NULL DEFAULT 'Omron_CIP',
  plc_ip_address        VARCHAR(45) NOT NULL DEFAULT '192.168.250.1',
  plc_port              INT NOT NULL DEFAULT 44818,
  plc_extras_json       JSON NULL COMMENT 'unit id, rack/slot, FINS nodes, AMS NetID, byte order',
  tagmap_profile        VARCHAR(100) NULL,
  daq_ip_address        VARCHAR(45) NULL DEFAULT '192.168.169.5',
  calibration_factor    FLOAT NOT NULL DEFAULT 1.0,
  updated_at            DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT chk_single_row CHECK (setting_id = 1)
);

CREATE TABLE wave_recipes (
  recipe_id            INT AUTO_INCREMENT PRIMARY KEY,
  test_number          VARCHAR(50) NOT NULL UNIQUE,
  wave_order           INT NOT NULL DEFAULT 1 COMMENT '1 or 2',
  wave_type            VARCHAR(50) NOT NULL COMMENT 'Monochromatic, Bichromatic, Random, Cnoidal, Solitary, Focusing, UserDefinedWave, UserDefinedStroke',
  spectral_shape       VARCHAR(50) DEFAULT 'None' COMMENT 'PM, GeneralizedPM, JONSWAP, TMA, Darbyshire',
  random_seed          INT DEFAULT 12345,
  gamma_factor         FLOAT DEFAULT 3.3,
  beta_factor          FLOAT DEFAULT 1.25,
  water_depth_m        FLOAT NOT NULL DEFAULT 0.6,
  signal_wave_height_m FLOAT NOT NULL DEFAULT 0.05,
  peak_wave_period_s   FLOAT NOT NULL DEFAULT 1.0,
  wave_angle_deg       FLOAT NOT NULL DEFAULT 0.0,
  num_waves            INT NOT NULL DEFAULT 10,
  sampling_freq_hz     FLOAT NOT NULL DEFAULT 125.0,
  signal_mod_pct       FLOAT NOT NULL DEFAULT 100.0,
  ramp_time_s          FLOAT NOT NULL DEFAULT 2.0,
  extra_params_json    JSON NULL COMMENT 'bichromatic H2/T2, focusing xf/tf, band limits, user-file refs',
  calc_stroke_m        FLOAT, calc_velocity_ms FLOAT, calc_accel_ms2 FLOAT,
  calc_force_kn        FLOAT, calc_power_w FLOAT, calc_wavelength_m FLOAT, calc_celerity_ms FLOAT,
  created_at           DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE test_runs (
  run_id                  INT AUTO_INCREMENT PRIMARY KEY,
  recipe_id               INT NOT NULL,
  start_timestamp         DATETIME(3) NOT NULL,
  end_timestamp           DATETIME(3) NULL,
  extra_record_duration_s FLOAT DEFAULT 1.0,
  run_status              ENUM('Running','Completed','E-Stopped','Fault') NOT NULL DEFAULT 'Running',
  h_1_3_m FLOAT, h_10_m FLOAT, h_max_m FLOAT, t_1_3_s FLOAT, t_10_s FLOAT, t_max_s FLOAT,
  limits_snapshot_json    JSON NULL COMMENT 'clamps, PLC profile and app version at run time',
  csv_file_path           VARCHAR(500) NOT NULL,
  pdf_report_path         VARCHAR(500) NULL,
  INDEX ix_run_recipe (recipe_id),
  CONSTRAINT fk_run_recipe FOREIGN KEY (recipe_id) REFERENCES wave_recipes(recipe_id) ON DELETE RESTRICT
);

CREATE TABLE telemetry_alerts (
  alert_id       INT AUTO_INCREMENT PRIMARY KEY,
  run_id         INT NULL,
  timestamp      DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  alert_severity ENUM('Warning','Critical','E-Stop') NOT NULL,
  alert_source   VARCHAR(50) NOT NULL COMMENT 'MathGatekeeper, PLC_Status, ServoDrive, WaveProbe, Comms',
  description    VARCHAR(500) NOT NULL,
  INDEX ix_alert_run_time (run_id, timestamp),
  CONSTRAINT fk_alert_run FOREIGN KEY (run_id) REFERENCES test_runs(run_id) ON DELETE SET NULL
);
```
Notes: recipe deletion is `RESTRICT` (run history must survive); store timestamps in UTC; the DB password is *not* in `system_settings`. `DatabaseService`: async, parameterised queries, transactions for multi-statement work, retry/reconnect, graceful **offline mode**. 100–200 Hz telemetry never goes to MySQL.

---

## 13. Reporting (`PdfReportService`, QuestPDF)
On run completion: parse the CSV, compute statistics (§7.12), build a multi-page A4 PDF: header (project, logo, date, run id, test number), input recipe, calculated kinematics, limit utilisation (% of stroke/velocity/acceleration), **wave-elevation time-series snapshot** (ScottPlot rendered off-screen to PNG bytes), measured spectrum, statistics table, alerts, page-numbered footer. Save path to `test_runs.pdf_report_path`. Run off the UI thread with progress.

---

## 14. Tests and acceptance (xUnit)

**Golden case (legacy screenshot; tolerance 1 %):** `h = 0.6 m, H = 0.05 m, T = 1 s`, 1st order, piston → `k ≈ 4.085 rad/m`, `L ≈ C ≈ 1.538`, `c0 ≈ 1.838`, `X_a ≈ 0.0136 m`, `u = πH/T = 0.15708 m/s`, `V_peak ≈ 0.0855 m/s`, `A_peak ≈ 0.537 m/s²`, wave power (W = 3 m) `≈ 7.6 W`, force estimate `≈ 177 N`.

Other tests: Newton solver converges for `T ∈ [0.3, 6]`, `h ∈ [0.2, 1.0]`; flap == elevated hinge at `h0 = 0`; `c0 → 2` as `kh → ∞` (`> 1.99` at `kh = 6`) and `c0 ≈ kh` as `kh → 0`; safety rejects `T = 0.29`, `X = 0.46 m`, `V = 1.6 m/s`, missing `a_max`; per-paddle phase formula; ramp endpoints; **spectra:** each shape integrates to `Hs²/16`, PM closed form matches, JONSWAP(γ=1) = PM, TMA → JONSWAP for `ω_h > 2`, generalised PM normalisation correct for β ∈ {0.8, 1.25, 2}; **random synthesis:** same seed → identical hash, band-limit energy report, realised Hs within tolerance after "scale to target"; zero-upcrossing statistics on synthetic sines and Rayleigh data; FINS/Modbus framing against hand-built byte arrays and a fake server; trajectory chunking (sequence, checksum, `MaxBlockWords`); heartbeat-loss → Smooth Stop in the simulator; DB repository tests (in-memory or container).

---

## 15. Delivery phases and output format

1. **Physics + tests (no UI):** Dispersion, transfer functions, trajectories for Monochromatic/Bichromatic/oblique, Gatekeeper, LimitCurve, WaveStatistics, golden tests.
2. **Simulator + PLC layer:** `IPlcDriver`, `DriverFactory`, `TagMap` + JSON profiles, Simulator (servo as 2nd-order lag with rate/stroke clamp; probes = linear Biesel response + propagation delay + noise), Modbus TCP, OPC UA, FINS, `PlcTelemetryService`, `CsvLogger`.
3. **WPF UI:** themes, three tabs, state machine, oscilloscopes against the simulator, limit curve, recipe CRUD, Controller Connection panel with Test Connection.
4. **All wave types:** Random (all spectra, Path A + B, band-limit, normalisation), Focusing, Solitary (Goring), User-defined; 2nd order monochromatic (Madsen), then general `F±/G±` (Schäffer); Cnoidal.
5. **Database, calibration wizard, PDF report, EtherNet/IP + S7 drivers, publish:** `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` plus Inno Setup script (bundle libplctag native DLL).

**Per-phase output:** (1) updated tree, (2) complete compilable source for new/changed files (no placeholders except flagged `[VERIFY]`), (3) `.csproj` / package changes, (4) build/run/test instructions, (5) acceptance checklist, (6) open questions.

---

## 16. Open inputs the agent must ask for (do not guess)
Maximum acceleration; servo encoder resolution; confirmed physical water-depth maximum; final PLC vendor and memory map / tag names; whether block-telemetry (PLC sample ring buffer) can be implemented in the PLC; number, x-positions and 4–20 mA ranges of probes; piston vs flap and hinge height; whether an absorber/beach exists; Darbyshire and second-order (`F±`, `G±`) reference formulas; the department's logo/report template; HBM/external DAQ protocol if still required.
