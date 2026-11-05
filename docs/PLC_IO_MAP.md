# PLC I/O map

Generated from `config/plc_profiles/*.json` by `PlcIoMap.ToMarkdown` — edit the profiles, then regenerate
(`WAVEFLUME_REGEN_DOCS=1 dotnet test`). Do not hand-edit.

## Safety — defence in depth

- The hardwired E-stop chain is the primary stop. The `EStop` command bit is an additional request only.
- **The PLC must enforce the same limits as the PC gatekeeper**: stroke clamp (≤ ±450 mm ceiling, default ±350 mm),
  velocity ≤ 1.57 m/s, the configured acceleration limit, and must refuse trajectories that break them.
- PC heartbeat (`HeartbeatPc`) toggles every telemetry cycle. If it stops changing for longer than the
  configured timeout while running, the PLC must Smooth Stop and set `FaultWord.CommsLoss`.
- The PLC heartbeat (`Heartbeat`) must change every PLC cycle; the PC Smooth Stops when it freezes.

## CmdWord bits (PC → PLC)

| Bit | Name |
|---|---|
| 0 | Enable |
| 1 | Home |
| 2 | Start |
| 3 | SmoothStop |
| 4 | EStop |
| 5 | Reset |
| 6 | ModeTrajectory |

Enable and ModeTrajectory are levels; the other bits are pulses — act on the rising edge.

## StatusWord bits (PLC → PC)

| Bit | Name |
|---|---|
| 0 | Enabled |
| 1 | Homed |
| 2 | Running |
| 3 | TrajectoryLoaded |
| 4 | Fault |
| 5 | EStopActive |
| 6 | SmoothStopping |

## FaultWord bits (PLC → PC)

| Bit | Name |
|---|---|
| 0 | CommsLoss |
| 1 | StrokeLimit |
| 2 | VelocityLimit |
| 3 | AccelLimit |
| 4 | FollowingError |
| 5 | DriveFault |
| 6 | ChunkError |

DriveFault and StrokeLimit are hard faults: the PC commands E-stop.

## Parametric mode

PC writes `Period_s`, `Ramp_s`, `Cycles` (number of waves), `Amp_mm[1..N]`, `Phase_deg[1..N]`, then `CmdWord` with
ModeTrajectory = 0. The PLC generates `X_p(t) = R(t)·Amp_p·sin(2πt/T + Phase_p)` with the half-cosine ramp up and down.

## Trajectory mode (chunk handshake)

1. Chunk 1 only: PC writes `Cycles` = total samples per paddle and sets ModeTrajectory = 1.
2. PC waits until `BufferLevel` (free samples in the PLC ring) ≥ samples in the chunk.
3. PC writes `TrajChunk` (mm, sample-major: s0p1..s0pN, s1p1..), `ChunkCrc`, `ChunkSeq`, then `ChunkReady` = 1.
4. PLC checks `ChunkSeq` = previous + 1 (chunk 1 restarts) and the CRC, copies the samples, clears `ChunkReady`,
   and sets `ChunkAck` = `ChunkSeq`. On a bad CRC or sequence it sets `FaultWord.ChunkError` and does not acknowledge.
5. The last chunk is padded by repeating its final sample; play only `Cycles` samples.
6. CRC: CRC-16/MODBUS (poly 0xA001 reflected, init 0xFFFF) over the chunk values as IEEE-754 float32
   **big-endian bytes**, independent of the wire word order. Check value of ASCII "123456789" = 0x4B37.
7. Samples are at `command_fs_hz`; the PLC interpolates to the servo cycle.

## Profile: Modbus TCP - generic (ModbusTcp)

32-bit word order: **ABCD**. BOOL occupies a whole word (0/1).

| Signal | Address | Words | Type | Length | Scale | Offset | R/W | Units |
|---|---|---|---|---|---|---|---|---|
| CmdWord | `HR0` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| HeartbeatPc | `HR1` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| Period_s | `HR2` | 2 | Float32 | 1 | 1 | 0 | W | s |
| Ramp_s | `HR4` | 2 | Float32 | 1 | 1 | 0 | W | s |
| Cycles | `HR6` | 2 | Int32 | 1 | 1 | 0 | W | waves \| samples (trajectory mode) |
| Amp_mm | `HR8` | 12 | Float32 | 6 | 1 | 0 | W | mm |
| Phase_deg | `HR20` | 12 | Float32 | 6 | 1 | 0 | W | deg |
| ChunkSeq | `HR40` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| ChunkReady | `HR41` | 1 | Bool | 1 | 1 | 0 | W |  |
| ChunkCrc | `HR42` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| ChunkAck | `HR43` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| BufferLevel | `HR44` | 2 | Int32 | 1 | 1 | 0 | R | free samples |
| Pos_mm | `HR100` | 12 | Float32 | 6 | 1 | 0 | R | mm |
| Probe_m | `HR200` | 16 | Float32 | 8 | 1 | 0 | R | mA (loop current; calibrated to m in the app) |
| StatusWord | `HR220` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| FaultWord | `HR221` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| Heartbeat | `HR222` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| SampleCounter | `HR224` | 2 | Int32 | 1 | 1 | 0 | R | samples |
| TrajChunk | `HR1000` | 120 | Float32 | 60 | 1 | 0 | W | mm |

## Profile: Omron NX/NJ - EtherNet/IP tags (EtherNetIp)

| Signal | Address | Type | Length | Scale | Offset | R/W | Units |
|---|---|---|---|---|---|---|---|
| CmdWord | `Wave.CmdWord` | UInt16 | 1 | 1 | 0 | W |  |
| HeartbeatPc | `Wave.HeartbeatPc` | UInt16 | 1 | 1 | 0 | W |  |
| Period_s | `Wave.Period` | Float32 | 1 | 1 | 0 | W | s |
| Ramp_s | `Wave.Ramp` | Float32 | 1 | 1 | 0 | W | s |
| Cycles | `Wave.Cycles` | Int32 | 1 | 1 | 0 | W | waves \| samples (trajectory mode) |
| Amp_mm | `Wave.Amp` | Float32 | 6 | 1 | 0 | W | mm |
| Phase_deg | `Wave.Phase` | Float32 | 6 | 1 | 0 | W | deg |
| ChunkSeq | `Wave.ChunkSeq` | UInt16 | 1 | 1 | 0 | W |  |
| ChunkReady | `Wave.ChunkReady` | Bool | 1 | 1 | 0 | W |  |
| ChunkCrc | `Wave.ChunkCrc` | UInt16 | 1 | 1 | 0 | W |  |
| ChunkAck | `Wave.ChunkAck` | UInt16 | 1 | 1 | 0 | R |  |
| BufferLevel | `Wave.BufferLevel` | Int32 | 1 | 1 | 0 | R | free samples |
| Pos_mm | `Wave.Pos` | Float32 | 6 | 1 | 0 | R | mm |
| Probe_m | `Wave.Probe` | Float32 | 8 | 1 | 0 | R | mA (loop current; calibrated to m in the app) |
| StatusWord | `Wave.StatusWord` | UInt16 | 1 | 1 | 0 | R |  |
| FaultWord | `Wave.FaultWord` | UInt16 | 1 | 1 | 0 | R |  |
| Heartbeat | `Wave.Heartbeat` | UInt16 | 1 | 1 | 0 | R |  |
| SampleCounter | `Wave.SampleCounter` | Int32 | 1 | 1 | 0 | R | samples |
| TrajChunk | `Wave.TrajChunk` | Float32 | 60 | 1 | 0 | W | mm |

## Profile: Omron FINS - DM map (OmronFins)

32-bit word order: **CDAB**. BOOL occupies a whole word (0/1).

| Signal | Address | Words | Type | Length | Scale | Offset | R/W | Units |
|---|---|---|---|---|---|---|---|---|
| CmdWord | `D0` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| HeartbeatPc | `D1` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| Period_s | `D2` | 2 | Float32 | 1 | 1 | 0 | W | s |
| Ramp_s | `D4` | 2 | Float32 | 1 | 1 | 0 | W | s |
| Cycles | `D6` | 2 | Int32 | 1 | 1 | 0 | W | waves \| samples (trajectory mode) |
| Amp_mm | `D8` | 12 | Float32 | 6 | 1 | 0 | W | mm |
| Phase_deg | `D20` | 12 | Float32 | 6 | 1 | 0 | W | deg |
| ChunkSeq | `D40` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| ChunkReady | `D41` | 1 | Bool | 1 | 1 | 0 | W |  |
| ChunkCrc | `D42` | 1 | UInt16 | 1 | 1 | 0 | W |  |
| ChunkAck | `D43` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| BufferLevel | `D44` | 2 | Int32 | 1 | 1 | 0 | R | free samples |
| Pos_mm | `D100` | 12 | Float32 | 6 | 1 | 0 | R | mm |
| Probe_m | `D200` | 16 | Float32 | 8 | 1 | 0 | R | mA (loop current; calibrated to m in the app) |
| StatusWord | `D220` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| FaultWord | `D221` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| Heartbeat | `D222` | 1 | UInt16 | 1 | 1 | 0 | R |  |
| SampleCounter | `D224` | 2 | Int32 | 1 | 1 | 0 | R | samples |
| TrajChunk | `D1000` | 120 | Float32 | 60 | 1 | 0 | W | mm |

## Profile: OPC UA - generic (OpcUa)

| Signal | Address | Type | Length | Scale | Offset | R/W | Units |
|---|---|---|---|---|---|---|---|
| CmdWord | `ns=3;s="DB_Wave"."CmdWord"` | UInt16 | 1 | 1 | 0 | W |  |
| HeartbeatPc | `ns=3;s="DB_Wave"."HeartbeatPc"` | UInt16 | 1 | 1 | 0 | W |  |
| Period_s | `ns=3;s="DB_Wave"."Period_s"` | Float32 | 1 | 1 | 0 | W | s |
| Ramp_s | `ns=3;s="DB_Wave"."Ramp_s"` | Float32 | 1 | 1 | 0 | W | s |
| Cycles | `ns=3;s="DB_Wave"."Cycles"` | Int32 | 1 | 1 | 0 | W | waves \| samples (trajectory mode) |
| Amp_mm | `ns=3;s="DB_Wave"."Amp_mm"` | Float32 | 6 | 1 | 0 | W | mm |
| Phase_deg | `ns=3;s="DB_Wave"."Phase_deg"` | Float32 | 6 | 1 | 0 | W | deg |
| ChunkSeq | `ns=3;s="DB_Wave"."ChunkSeq"` | UInt16 | 1 | 1 | 0 | W |  |
| ChunkReady | `ns=3;s="DB_Wave"."ChunkReady"` | Bool | 1 | 1 | 0 | W |  |
| ChunkCrc | `ns=3;s="DB_Wave"."ChunkCrc"` | UInt16 | 1 | 1 | 0 | W |  |
| ChunkAck | `ns=3;s="DB_Wave"."ChunkAck"` | UInt16 | 1 | 1 | 0 | R |  |
| BufferLevel | `ns=3;s="DB_Wave"."BufferLevel"` | Int32 | 1 | 1 | 0 | R | free samples |
| Pos_mm | `ns=3;s="DB_Wave"."Pos_mm"` | Float32 | 6 | 1 | 0 | R | mm |
| Probe_m | `ns=3;s="DB_Wave"."Probe_m"` | Float32 | 8 | 1 | 0 | R | mA (loop current; calibrated to m in the app) |
| StatusWord | `ns=3;s="DB_Wave"."StatusWord"` | UInt16 | 1 | 1 | 0 | R |  |
| FaultWord | `ns=3;s="DB_Wave"."FaultWord"` | UInt16 | 1 | 1 | 0 | R |  |
| Heartbeat | `ns=3;s="DB_Wave"."Heartbeat"` | UInt16 | 1 | 1 | 0 | R |  |
| SampleCounter | `ns=3;s="DB_Wave"."SampleCounter"` | Int32 | 1 | 1 | 0 | R | samples |
| TrajChunk | `ns=3;s="DB_Wave"."TrajChunk"` | Float32 | 60 | 1 | 0 | W | mm |

## Profile: Siemens S7-1500 - DB10 (S7)

| Signal | Address | Type | Length | Scale | Offset | R/W | Units |
|---|---|---|---|---|---|---|---|
| CmdWord | `DB10.DBW0` | UInt16 | 1 | 1 | 0 | W |  |
| HeartbeatPc | `DB10.DBW2` | UInt16 | 1 | 1 | 0 | W |  |
| Period_s | `DB10.DBD4` | Float32 | 1 | 1 | 0 | W | s |
| Ramp_s | `DB10.DBD8` | Float32 | 1 | 1 | 0 | W | s |
| Cycles | `DB10.DBD12` | Int32 | 1 | 1 | 0 | W | waves \| samples (trajectory mode) |
| Amp_mm | `DB10.DBD16` | Float32 | 6 | 1 | 0 | W | mm |
| Phase_deg | `DB10.DBD40` | Float32 | 6 | 1 | 0 | W | deg |
| ChunkSeq | `DB10.DBW80` | UInt16 | 1 | 1 | 0 | W |  |
| ChunkReady | `DB10.DBX82.0` | Bool | 1 | 1 | 0 | W |  |
| ChunkCrc | `DB10.DBW84` | UInt16 | 1 | 1 | 0 | W |  |
| ChunkAck | `DB10.DBW86` | UInt16 | 1 | 1 | 0 | R |  |
| BufferLevel | `DB10.DBD88` | Int32 | 1 | 1 | 0 | R | free samples |
| Pos_mm | `DB10.DBD200` | Float32 | 6 | 1 | 0 | R | mm |
| Probe_m | `DB10.DBD400` | Float32 | 8 | 1 | 0 | R | mA (loop current; calibrated to m in the app) |
| StatusWord | `DB10.DBW440` | UInt16 | 1 | 1 | 0 | R |  |
| FaultWord | `DB10.DBW442` | UInt16 | 1 | 1 | 0 | R |  |
| Heartbeat | `DB10.DBW444` | UInt16 | 1 | 1 | 0 | R |  |
| SampleCounter | `DB10.DBD448` | Int32 | 1 | 1 | 0 | R | samples |
| TrajChunk | `DB10.DBD2000` | Float32 | 60 | 1 | 0 | W | mm |

