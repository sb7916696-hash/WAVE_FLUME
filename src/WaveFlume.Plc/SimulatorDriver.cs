using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Plc;

public sealed record SimulatorOptions
{
    public FlumeConfig Flume { get; init; } = new();
    public double WaterDepthM { get; init; } = 0.6;

    /// <summary>Probe distances from the paddles, m (≤ 8).</summary>
    public double[] ProbeXM { get; init; } = [5, 10, 20];
    public double ProbeNoiseM { get; init; } = 0.0005;
    public int NoiseSeed { get; init; } = 1;

    /// <summary>Servo modelled as a 2nd-order lag with rate and stroke clamp.</summary>
    public double ServoNaturalHz { get; init; } = 8.0;
    public double ServoDamping { get; init; } = 0.9;
    public int ServoSubsteps { get; init; } = 8;

    public double HeartbeatTimeoutS { get; init; } = 1.0;
    public double SmoothStopS { get; init; } = 2.0;
    public double SettleTailS { get; init; } = 1.0;

    /// <summary>PLC telemetry ring depth for block mode, samples.</summary>
    public int RingCapacitySamples { get; init; } = 1000;
    public int MaxBlockWords { get; init; } = 120;

    /// <summary>Slowest group velocity considered when padding the probe FFT, m/s.</summary>
    public double MinGroupVelocityMs { get; init; } = 0.3;
}

/// <summary>
/// Virtual flume (spec §3 rule 3): accepts recipes and chunks exactly like a PLC, enforces the same
/// limits, and produces paddle and probe telemetry. Time comes from <see cref="TimeProvider"/>, and all
/// state is evaluated lazily from sample counters, so tests are deterministic.
/// </summary>
public sealed class SimulatorDriver(SimulatorOptions opt, TimeProvider? time = null) : IPlcDriver
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly Random _noise = new(opt.NoiseSeed);
    private readonly List<float> _chunkValues = [];

    private long _t0;
    private StatusBits _status;
    private FaultBits _fault;
    private ushort _lastSeq;
    private int _totalSamples;
    private float[][]? _commanded;          // m, at command fs
    private float[][]? _actualMm;           // servo response, at telemetry rate
    private float[][]? _probesM;            // at telemetry rate
    private float[] _holdMm = new float[opt.Flume.NumPaddles];
    private long _runStart = -1, _stopStart = -1, _lastHeartbeat, _lastBlock = -1;

    private int Paddles => opt.Flume.NumPaddles;
    private double Fs => opt.Flume.Limits.TelemetryRateHz;
    private double CommandFs => opt.Flume.Limits.CommandFsHz;

    /// <summary>Depth used by the probe model; the app updates it when a recipe is sent.</summary>
    public double WaterDepthM { get; set; } = opt.WaterDepthM;

    public PlcProtocolType Protocol => PlcProtocolType.Simulator;
    public DriverCapabilities Capabilities => new(opt.MaxBlockWords, true, true);
    public ConnectionState State { get; private set; }
    public long CommErrorCount => 0;
    public string? LastError { get; private set; }
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public Task<ConnectResult> ConnectAsync(PlcEndpoint endpoint, CancellationToken ct)
    {
        lock (_lock)
        {
            _t0 = _time.GetTimestamp();
            _holdMm = new float[Paddles];
            _lastHeartbeat = 0;
        }
        SetState(ConnectionState.Connected);
        return Task.FromResult(ConnectResult.Success(0));
    }

    public Task DisconnectAsync()
    {
        SetState(ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<PingResult> PingAsync(CancellationToken ct)
    {
        lock (_lock) return Task.FromResult(new PingResult(true, FailureReason.None, "Simulator", 0, (double)StatusWord()));
    }

    public Task<bool> SendWaveRecipeAsync(WaveCommandPacket p, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!Idle("load a recipe")) return Task.FromResult(false);
            int n = (int)Math.Round(p.Cycles * p.PeriodS * CommandFs) + 1;
            double d = (n - 1) / CommandFs, w = 2 * Math.PI / p.PeriodS;
            var traj = new float[Paddles][];
            for (int k = 0; k < Paddles; k++)
            {
                traj[k] = new float[n];
                double amp = Units.MmToM(p.AmplitudeMm[k]), ph = Units.DegToRad(p.PhaseDeg[k]);
                for (int i = 0; i < n; i++)
                    traj[k][i] = (float)(amp * Ramp.Envelope(i / CommandFs, d, p.RampS) * Math.Sin(w * i / CommandFs + ph));
            }
            return Task.FromResult(Load(traj));
        }
    }

    public Task<bool> UploadTrajectoryChunkAsync(TrajectoryChunk chunk, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!Idle("upload a trajectory")) return Task.FromResult(false);
            if (chunk.Seq == 1)
            {
                _chunkValues.Clear();
                _totalSamples = chunk.TotalSamples;
                _status &= ~StatusBits.TrajectoryLoaded;
            }
            else if (chunk.Seq != unchecked((ushort)(_lastSeq + 1)))
                return Task.FromResult(Reject(FaultBits.ChunkError, $"Chunk sequence {chunk.Seq} after {_lastSeq}."));
            if (Crc16.OfFloats(chunk.ValuesMm) != chunk.Crc)
                return Task.FromResult(Reject(FaultBits.ChunkError, $"Chunk {chunk.Seq} CRC mismatch."));
            if (chunk.ValuesMm.Length % Paddles != 0 || chunk.ValuesMm.Length > opt.MaxBlockWords / 2)
                return Task.FromResult(Reject(FaultBits.ChunkError, $"Chunk {chunk.Seq} has a bad length."));

            _lastSeq = chunk.Seq;
            _chunkValues.AddRange(chunk.ValuesMm);
            if (_chunkValues.Count < _totalSamples * Paddles) return Task.FromResult(true);

            var traj = new float[Paddles][];
            for (int k = 0; k < Paddles; k++)
            {
                traj[k] = new float[_totalSamples];
                for (int i = 0; i < _totalSamples; i++) traj[k][i] = (float)Units.MmToM(_chunkValues[i * Paddles + k]);
            }
            return Task.FromResult(Load(traj));
        }
    }

    public Task<bool> SendControlCommandAsync(PlcControlCommand cmd, CancellationToken ct)
    {
        lock (_lock)
        {
            long c = Counter();
            Advance(c);
            bool ok = cmd switch
            {
                PlcControlCommand.Enable => !_status.HasFlag(StatusBits.EStopActive) && Set(StatusBits.Enabled),
                PlcControlCommand.Disable => Idle("disable servos") && Clear(StatusBits.Enabled | StatusBits.Homed),
                PlcControlCommand.Home => _status.HasFlag(StatusBits.Enabled) && Idle("home") && Home(),
                PlcControlCommand.Start => StartRun(c),
                PlcControlCommand.SmoothStop => BeginSmoothStop(c),
                PlcControlCommand.EStop => EStop(c),
                PlcControlCommand.Reset => Reset(),
                _ => false,
            };
            if (!ok) LastError ??= $"{cmd} refused in state {(StatusBits)StatusWord()}.";
            return Task.FromResult(ok);
        }
    }

    public Task SendHeartbeatAsync(ushort value, CancellationToken ct)
    {
        lock (_lock)
        {
            long c = Counter();
            Advance(c); // a heartbeat arriving after the deadline does not cancel the fault
            _lastHeartbeat = c;
        }
        return Task.CompletedTask;
    }

    public Task<TelemetrySnapshot> PollTelemetryAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            long c = Counter();
            Advance(c);
            return Task.FromResult(Sample(c));
        }
    }

    public Task<TelemetryBlock> ReadTelemetryBlockAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            long c = Counter();
            Advance(c);
            long first = Math.Max(_lastBlock + 1, c - opt.RingCapacitySamples + 1);
            long lost = Math.Max(0, first - (_lastBlock + 1));
            var samples = new List<TelemetrySnapshot>((int)Math.Max(0, c - first + 1));
            for (long i = first; i <= c; i++) samples.Add(Sample(i));
            _lastBlock = c;
            return Task.FromResult(new TelemetryBlock(samples, lost));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- state machine ----------------------------------------------------------------------

    private long Counter() => (long)(_time.GetElapsedTime(_t0).TotalSeconds * Fs);

    private ushort StatusWord() => (ushort)(_status | (_fault != 0 ? StatusBits.Fault : 0));

    private bool Running => _status.HasFlag(StatusBits.Running);

    private bool Set(StatusBits b) { _status |= b; return true; }
    private bool Clear(StatusBits b) { _status &= ~b; return true; }

    private bool Idle(string action)
    {
        if (!Running) return true;
        LastError = $"Cannot {action} while running.";
        return false;
    }

    private bool Reject(FaultBits fault, string message)
    {
        _fault |= fault;
        LastError = message;
        return false;
    }

    /// <summary>Defence in depth: the "PLC" refuses trajectories outside the stroke clamp.</summary>
    private bool Load(float[][] traj)
    {
        double clamp = Math.Min(opt.Flume.Limits.MaxStrokeM, HardLimits.MaxStrokeCeilingM);
        if (traj.Any(row => row.Any(x => Math.Abs(x) > clamp)))
            return Reject(FaultBits.StrokeLimit, "Trajectory exceeds the stroke clamp.");
        _commanded = traj;
        return Set(StatusBits.TrajectoryLoaded);
    }

    private bool Home()
    {
        _holdMm = new float[Paddles];
        return Set(StatusBits.Homed);
    }

    private bool StartRun(long c)
    {
        const StatusBits ready = StatusBits.Enabled | StatusBits.Homed | StatusBits.TrajectoryLoaded;
        if ((_status & ready) != ready || _fault != 0 || Running || _commanded is null) return false;
        (_actualMm, _probesM) = Precompute(_commanded);
        _runStart = c;
        _stopStart = -1;
        _lastHeartbeat = c;
        return Set(StatusBits.Running);
    }

    private bool BeginSmoothStop(long c)
    {
        if (!Running) return false;
        if (_stopStart < 0) _stopStart = c;
        return Set(StatusBits.SmoothStopping);
    }

    private bool EStop(long c)
    {
        _holdMm = PaddlesAt(c);
        _runStart = -1;
        _status = (_status & StatusBits.TrajectoryLoaded) | StatusBits.EStopActive;
        return true;
    }

    private bool Reset()
    {
        _fault = FaultBits.None;
        _status &= ~(StatusBits.EStopActive | StatusBits.Fault);
        LastError = null;
        return true;
    }

    /// <summary>Applies time-driven transitions at their exact sample counters.</summary>
    private void Advance(long c)
    {
        if (!Running) return;
        long hbDeadline = _lastHeartbeat + (long)(opt.HeartbeatTimeoutS * Fs);
        if (c > hbDeadline && _stopStart < 0)
        {
            _fault |= FaultBits.CommsLoss;
            BeginSmoothStop(hbDeadline);
        }
        bool stopDone = _stopStart >= 0 && c >= _stopStart + (long)(opt.SmoothStopS * Fs);
        bool runDone = c - _runStart >= _actualMm![0].Length;
        if (stopDone || runDone)
        {
            _holdMm = stopDone ? new float[Paddles] : PaddlesAt(_runStart + _actualMm[0].Length - 1);
            _status &= ~(StatusBits.Running | StatusBits.SmoothStopping);
        }
    }

    private double StopEnvelope(long c) =>
        _stopStart < 0 ? 1 : Ramp.Up(opt.SmoothStopS - (c - _stopStart) / Fs, opt.SmoothStopS);

    private float[] PaddlesAt(long c)
    {
        long i = c - _runStart;
        if (_runStart < 0 || _actualMm is null || i < 0 || i >= _actualMm[0].Length || !Running) return (float[])_holdMm.Clone();
        double env = StopEnvelope(c);
        return Array.ConvertAll(_actualMm, row => (float)(row[i] * env));
    }

    private TelemetrySnapshot Sample(long c)
    {
        var probes = new float[opt.ProbeXM.Length];
        long i = c - _runStart;
        double env = StopEnvelope(c);
        for (int k = 0; k < probes.Length; k++)
        {
            double eta = _runStart >= 0 && _probesM is not null && i >= 0 && i < _probesM[k].Length ? _probesM[k][i] * env : 0;
            probes[k] = (float)(eta + opt.ProbeNoiseM * Gaussian());
        }
        // Probe "hardware": the nominal 4–20 mA range from the config (what an uncalibrated channel assumes).
        var nominal = opt.Flume.Probes;
        var raw = Array.ConvertAll(probes, m => (float)((m - nominal.NominalOffsetM) / nominal.NominalGainMPerMa));
        return new TelemetrySnapshot(c / Fs, c, PaddlesAt(c), probes, StatusWord(), (ushort)_fault, unchecked((ushort)c)) { ProbeRawMa = raw };
    }

    private double Gaussian() =>
        Math.Sqrt(-2 * Math.Log(1 - _noise.NextDouble())) * Math.Cos(2 * Math.PI * _noise.NextDouble());

    // ---- physics ------------------------------------------------------------------------------

    /// <summary>Servo response at the telemetry rate, then probe signals by linear Biesel transfer.</summary>
    private (float[][] ActualMm, float[][] ProbesM) Precompute(float[][] commanded)
    {
        int n = (int)Math.Round((commanded[0].Length - 1) / CommandFs * Fs) + 1 + (int)(opt.SettleTailS * Fs);
        var actual = new double[Paddles][];
        for (int p = 0; p < Paddles; p++) actual[p] = Servo(commanded[p], n);

        var mean = new double[n];
        foreach (var row in actual)
            for (int i = 0; i < n; i++) mean[i] += row[i] / Paddles;
        var probes = opt.ProbeXM.Select(x => ProbeResponse(mean, x)).ToArray();

        return (actual.Select(r => Array.ConvertAll(r, v => (float)Units.MToMm(v))).ToArray(), probes);
    }

    private double[] Servo(float[] commanded, int n)
    {
        double wn = 2 * Math.PI * opt.ServoNaturalHz, z = opt.ServoDamping;
        double dt = 1 / Fs / opt.ServoSubsteps;
        double vMax = Math.Min(opt.Flume.Limits.MaxVelocityMs, HardLimits.MaxVelocityCeilingMs);
        double xMax = Math.Min(opt.Flume.Limits.MaxStrokeM, HardLimits.MaxStrokeCeilingM);
        double x = 0, v = 0;
        var y = new double[n];
        for (int i = 0; i < n; i++)
        {
            int ci = Math.Min((int)Math.Round(i / Fs * CommandFs), commanded.Length - 1);
            double u = commanded[ci];
            for (int s = 0; s < opt.ServoSubsteps; s++)
            {
                v += (wn * wn * (u - x) - 2 * z * wn * v) * dt;
                v = Math.Clamp(v, -vMax, vMax);
                x = Math.Clamp(x + v * dt, -xMax, xMax);
            }
            y[i] = x;
        }
        return y;
    }

    /// <summary>
    /// η(x,t) from paddle motion: per frequency, X = X_a sin ωt ⇒ η = c0 X_a cos(kx − ωt),
    /// i.e. multiply the positive-frequency bin by i·c0·e^{−ikx}. Evanescent modes and reflections ignored.
    /// </summary>
    private float[] ProbeResponse(double[] paddle, double x)
    {
        int pad = (int)(x / opt.MinGroupVelocityMs * Fs);
        int len = 1;
        while (len < paddle.Length + pad) len <<= 1;
        var spec = new Complex[len];
        for (int i = 0; i < paddle.Length; i++) spec[i] = paddle[i];
        Fourier.Forward(spec, FourierOptions.AsymmetricScaling);

        double peak = spec.Max(c => c.Magnitude);
        double h = WaterDepthM, g = opt.Flume.Gravity;
        for (int b = 1; b <= len / 2; b++)
        {
            if (spec[b].Magnitude < 1e-9 * peak) { spec[b] = 0; spec[len - b] = 0; continue; }
            var w = Dispersion.Solve(len / (b * Fs), h, g);
            double c0 = TransferFunctions.C0(opt.Flume.PaddleType, w.K, h, opt.Flume.HingeHeightM);
            // MathNet's forward transform uses e^{-iωt}: bin b is the e^{+iω_b t} coefficient.
            var m = Complex.ImaginaryOne * c0 * Complex.FromPolarCoordinates(1, -w.K * x);
            spec[b] *= m;
            if (b != len - b) spec[len - b] *= Complex.Conjugate(m);
        }
        spec[0] = 0;
        Fourier.Inverse(spec, FourierOptions.AsymmetricScaling);
        return spec.Take(paddle.Length + pad).Select(c => (float)c.Real).ToArray();
    }

    private void SetState(ConnectionState s)
    {
        State = s;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(s, "Simulator"));
    }
}
