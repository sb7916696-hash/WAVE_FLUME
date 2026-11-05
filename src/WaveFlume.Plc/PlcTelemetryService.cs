using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WaveFlume.Core;

namespace WaveFlume.Plc;

public sealed record TelemetryOptions
{
    /// <summary>Single-sample poll rate (poll mode), Hz.</summary>
    public double PollRateHz { get; init; } = 125;

    /// <summary>Block read rate (block mode), Hz — spec: 20–50.</summary>
    public double BlockReadHz { get; init; } = 50;
    public double HeartbeatTimeoutS { get; init; } = 1.0;
    public double StrokeClampM { get; init; } = 0.35;
    public double PositionGuardFraction { get; init; } = 0.95;
    public int CsvChannelCapacity { get; init; } = 20_000;
    public int UiBufferCapacity { get; init; } = 60 * 200;
    public int MaxReconnectDelayMs { get; init; } = 5000;
    public int InitialReconnectDelayMs { get; init; } = 100;

    /// <summary>Probe channels checked for 4–20 mA loop faults (0 = none).</summary>
    public int ActiveProbes { get; init; }
    public double ProbeFaultLowMa { get; init; } = 3.8;
    public double ProbeFaultHighMa { get; init; } = 20.5;
}

public sealed record TimingStats(long Cycles, long MissedCycles, double MaxJitterMs, long LostSamples);

/// <summary>Fixed-size drop-oldest buffer for the oscilloscope views.</summary>
public sealed class SampleRingBuffer<T>(int capacity)
{
    private readonly T[] _items = new T[capacity];
    private readonly object _lock = new();
    private int _next, _count;

    public void Add(T item)
    {
        lock (_lock)
        {
            _items[_next] = item;
            _next = (_next + 1) % _items.Length;
            _count = Math.Min(_count + 1, _items.Length);
        }
    }

    /// <summary>Most recent <paramref name="max"/> items, oldest first.</summary>
    public T[] Latest(int max)
    {
        lock (_lock)
        {
            int n = Math.Min(max, _count);
            var result = new T[n];
            for (int i = 0; i < n; i++) result[i] = _items[(_next - n + i + _items.Length) % _items.Length];
            return result;
        }
    }
}

/// <summary>
/// Telemetry loop on a dedicated thread (spec §5). Feeds the UI ring buffer and the CSV channel,
/// toggles the PC heartbeat, and runs the runtime guard (spec §8): position near the clamp or PLC
/// heartbeat loss → Critical + Smooth Stop; PLC hard fault → E-stop. Reconnects with back-off.
/// </summary>
public sealed class PlcTelemetryService : IAsyncDisposable
{
    private readonly IPlcDriver _driver;
    private readonly TelemetryOptions _opt;
    private readonly TimeProvider _time;
    private volatile Channel<TelemetrySnapshot>? _csv;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private PlcEndpoint _endpoint = new();

    private ushort _heartbeatOut;
    private ushort? _lastPlcHeartbeat;
    private long _lastHeartbeatChange;
    private long _lastCounter = -1;
    private bool _heartbeatAlarm, _positionAlarm, _hardFaultAlarm, _commsAlarm;
    private readonly HashSet<int> _probeFaults = [];
    private ushort _lastFault;
    private long _cycles, _missed, _lost;
    private double _maxJitterMs;

    public PlcTelemetryService(IPlcDriver driver, TelemetryOptions options, TimeProvider? time = null)
    {
        _driver = driver;
        _opt = options;
        _time = time ?? TimeProvider.System;
        UiBuffer = new SampleRingBuffer<TelemetrySnapshot>(options.UiBufferCapacity);
        _lastHeartbeatChange = _time.GetTimestamp();
    }

    /// <summary>
    /// Starts routing samples to a new CSV channel (one per run). Between captures nothing is queued,
    /// so a bounded channel nobody reads can never stall the telemetry thread.
    /// </summary>
    public ChannelReader<TelemetrySnapshot> BeginCsvCapture()
    {
        var ch = Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(_opt.CsvChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        Interlocked.Exchange(ref _csv, ch)?.Writer.TryComplete();
        return ch.Reader;
    }

    /// <summary>Completes the current capture; the CSV logger drains what is queued and returns.</summary>
    public void EndCsvCapture() => Interlocked.Exchange(ref _csv, null)?.Writer.TryComplete();
    public SampleRingBuffer<TelemetrySnapshot> UiBuffer { get; }
    public TimingStats Timing => new(Interlocked.Read(ref _cycles), Interlocked.Read(ref _missed), _maxJitterMs, Interlocked.Read(ref _lost));
    public event EventHandler<Alert>? AlertRaised;

    /// <summary>Raw mA → metres (probe calibration). Applied to every sample that carries raw probe current.</summary>
    public Func<float[], float[]>? ProbeConverter { get; set; }

    private bool BlockMode => _driver.Capabilities.SupportsBlockTelemetry;

    public void Start(PlcEndpoint endpoint)
    {
        if (_thread is not null) throw new InvalidOperationException("Already running.");
        _endpoint = endpoint;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true, Name = "PlcTelemetry", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        return Task.Run(() =>
        {
            _thread?.Join();
            _thread = null;
            EndCsvCapture();
        });
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>One telemetry cycle. Public so tests can drive it without the timing thread.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        _heartbeatOut = (ushort)(_heartbeatOut == 0 ? 1 : 0);
        await _driver.SendHeartbeatAsync(_heartbeatOut, ct).ConfigureAwait(false);

        IReadOnlyList<TelemetrySnapshot> samples = BlockMode
            ? (await _driver.ReadTelemetryBlockAsync(ct).ConfigureAwait(false)).Samples
            : [await _driver.PollTelemetryAsync(ct).ConfigureAwait(false)];

        foreach (var s in samples)
        {
            if (BlockMode && _lastCounter >= 0)
            {
                if (s.SampleCounter <= _lastCounter) continue;
                Interlocked.Add(ref _lost, s.SampleCounter - _lastCounter - 1);
            }
            _lastCounter = s.SampleCounter;
            var sample = s.ProbeRawMa.Length > 0 && ProbeConverter is { } convert ? s with { ProbeM = convert(s.ProbeRawMa) } : s;
            UiBuffer.Add(sample);
            if (_csv is { } csv)
                while (!csv.Writer.TryWrite(sample))
                    if (!await csv.Writer.WaitToWriteAsync(ct).ConfigureAwait(false)) break;
            await GuardAsync(sample, ct).ConfigureAwait(false);
        }
        if (_commsAlarm)
        {
            _commsAlarm = false;
            Raise(AlertSeverity.Warning, AlertSources.Comms, "Communication restored.");
        }
    }

    private async Task GuardAsync(TelemetrySnapshot s, CancellationToken ct)
    {
        long now = _time.GetTimestamp();
        if (s.Heartbeat != _lastPlcHeartbeat)
        {
            _lastPlcHeartbeat = s.Heartbeat;
            _lastHeartbeatChange = now;
            _heartbeatAlarm = false;
        }
        else if (!_heartbeatAlarm && _time.GetElapsedTime(_lastHeartbeatChange, now).TotalSeconds > _opt.HeartbeatTimeoutS)
        {
            _heartbeatAlarm = true;
            Raise(AlertSeverity.Critical, AlertSources.Comms, "PLC heartbeat lost; Smooth Stop commanded.");
            await _driver.SendControlCommandAsync(PlcControlCommand.SmoothStop, ct).ConfigureAwait(false);
        }

        double limitMm = Units.MToMm(_opt.StrokeClampM * _opt.PositionGuardFraction);
        int paddle = Array.FindIndex(s.PaddleMm, x => Math.Abs(x) > limitMm);
        if (paddle >= 0 && !_positionAlarm)
        {
            _positionAlarm = true;
            Raise(AlertSeverity.Critical, AlertSources.ServoDrive,
                $"Paddle {paddle + 1} at {s.PaddleMm[paddle]:0.0} mm > {_opt.PositionGuardFraction:P0} of clamp; Smooth Stop commanded.");
            await _driver.SendControlCommandAsync(PlcControlCommand.SmoothStop, ct).ConfigureAwait(false);
        }
        else if (paddle < 0) _positionAlarm = false;

        var fault = (FaultBits)s.FaultWord;
        if ((fault & FaultBits.HardFaultMask) != 0 && !_hardFaultAlarm)
        {
            _hardFaultAlarm = true;
            Raise(AlertSeverity.EStop, AlertSources.PlcStatus, $"PLC hard fault ({fault}); E-stop commanded.");
            await _driver.SendControlCommandAsync(PlcControlCommand.EStop, ct).ConfigureAwait(false);
        }
        else if ((fault & FaultBits.HardFaultMask) == 0) _hardFaultAlarm = false;

        for (int ch = 0; ch < Math.Min(_opt.ActiveProbes, s.ProbeRawMa.Length); ch++)
        {
            double ma = s.ProbeRawMa[ch];
            bool loopFault = ma < _opt.ProbeFaultLowMa || ma > _opt.ProbeFaultHighMa;
            if (loopFault && _probeFaults.Add(ch))
            {
                Raise(AlertSeverity.Critical, AlertSources.WaveProbe,
                    $"Probe {ch + 1} loop fault: {ma:0.00} mA outside [{_opt.ProbeFaultLowMa}, {_opt.ProbeFaultHighMa}] mA; Smooth Stop commanded.");
                await _driver.SendControlCommandAsync(PlcControlCommand.SmoothStop, ct).ConfigureAwait(false);
            }
            else if (!loopFault) _probeFaults.Remove(ch);
        }

        var newBits = (FaultBits)(s.FaultWord & ~_lastFault) & ~FaultBits.HardFaultMask;
        if (newBits != 0)
            Raise(AlertSeverity.Critical, AlertSources.PlcStatus, $"PLC fault: {newBits}.");
        _lastFault = s.FaultWord;
    }

    private void Raise(AlertSeverity severity, string source, string text) =>
        AlertRaised?.Invoke(this, new Alert(_time.GetUtcNow().UtcDateTime, severity, source, text));

    private void Run(CancellationToken ct)
    {
        bool hiRes = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        try
        {
            var sw = Stopwatch.StartNew();
            var period = TimeSpan.FromSeconds(1 / (BlockMode ? _opt.BlockReadHz : _opt.PollRateHz));
            var next = sw.Elapsed;
            int reconnectDelay = _opt.InitialReconnectDelayMs;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TickAsync(ct).GetAwaiter().GetResult();
                    reconnectDelay = _opt.InitialReconnectDelayMs;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!_commsAlarm)
                    {
                        _commsAlarm = true;
                        Raise(AlertSeverity.Critical, AlertSources.Comms, $"Telemetry failed: {ex.Message}. Reconnecting.");
                    }
                    if (ct.WaitHandle.WaitOne(reconnectDelay)) break;
                    reconnectDelay = Math.Min(reconnectDelay * 2, _opt.MaxReconnectDelayMs);
                    try { _driver.ConnectAsync(_endpoint, ct).GetAwaiter().GetResult(); }
                    catch (Exception) when (!ct.IsCancellationRequested) { /* retried next cycle */ }
                    next = sw.Elapsed;
                    continue;
                }

                Interlocked.Increment(ref _cycles);
                next += period;
                var late = sw.Elapsed - next;
                if (late > period)
                {
                    Interlocked.Add(ref _missed, (long)(late / period));
                    next = sw.Elapsed;
                }
                while (sw.Elapsed < next - TimeSpan.FromMilliseconds(2)) Thread.Sleep(1);
                while (sw.Elapsed < next) Thread.SpinWait(50);
                _maxJitterMs = Math.Max(_maxJitterMs, (sw.Elapsed - next).TotalMilliseconds);
            }
        }
        finally
        {
            if (hiRes) TimeEndPeriod(1);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);
}
