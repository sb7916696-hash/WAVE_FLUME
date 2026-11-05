using System.Diagnostics;
using System.Net.Sockets;
using WaveFlume.Core;

namespace WaveFlume.Plc;

public sealed class PlcProtocolException(FailureReason reason, string message) : Exception(message)
{
    public FailureReason Reason { get; } = reason;
}

/// <summary>
/// Implements <see cref="IPlcDriver"/> purely through the TagMap. A protocol only supplies
/// connect/disconnect, tag read/write and an address check — changing PLC brand needs no recompile.
/// </summary>
public abstract class TagMapDriverBase(TagMap map) : IPlcDriver
{
    /// <summary>Poll interval while waiting for ChunkAck / BufferLevel.</summary>
    public const int HandshakePollMs = 5;

    /// <summary>
    /// Windows retries a TCP SYN for ~2 s after an RST, so a shorter connect timeout would report a
    /// refused port as "timeout". Applies to the TCP connect only, not to request timeouts.
    /// </summary>
    public const int MinConnectTimeoutMs = 3000;

    protected static int ConnectTimeoutMs(PlcEndpoint ep) => Math.Max(ep.TimeoutMs, MinConnectTimeoutMs);

    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CommandBits _cmd;
    private long _errors;

    protected TagMap Map => map;
    protected PlcEndpoint Endpoint { get; private set; } = new();

    public abstract PlcProtocolType Protocol { get; }
    public abstract DriverCapabilities Capabilities { get; }
    public ConnectionState State { get; private set; }
    public long CommErrorCount => Interlocked.Read(ref _errors);
    public string? LastError { get; private set; }
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    protected abstract Task ConnectCoreAsync(PlcEndpoint endpoint, CancellationToken ct);
    protected abstract Task DisconnectCoreAsync();
    protected abstract Task<double[]> ReadTagCoreAsync(TagEntry tag, CancellationToken ct);
    protected abstract Task WriteTagCoreAsync(TagEntry tag, double[] values, CancellationToken ct);

    /// <summary>Protocol-specific address check for the TagMap editor; null when valid.</summary>
    public abstract string? CheckAddress(TagEntry tag);

    /// <summary>Connect is refused while this returns errors (spec §9.5).</summary>
    public virtual IReadOnlyList<string> ValidateTagMap() => map.Validate(CheckAddress);

    public async Task<ConnectResult> ConnectAsync(PlcEndpoint endpoint, CancellationToken ct)
    {
        var errors = ValidateTagMap();
        if (errors.Count > 0)
            return ConnectResult.Fail(FailureReason.InvalidConfig, $"TagMap '{map.Name}' invalid: {errors[0]}");

        Endpoint = endpoint;
        SetState(ConnectionState.Connecting, null);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            await ConnectCoreAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (reason, msg) = Classify(ex);
            RecordError(msg);
            return ConnectResult.Fail(reason, msg);
        }

        var ping = await PingAsync(ct).ConfigureAwait(false);
        if (!ping.Ok)
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            return ConnectResult.Fail(ping.Reason, ping.Message);
        }
        SetState(ConnectionState.Connected, $"RTT {ping.RttMs:0.0} ms");
        return ConnectResult.Success(ping.RttMs);
    }

    public async Task DisconnectAsync()
    {
        await DisconnectCoreAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, null);
    }

    /// <summary>Protocol handshake + one test-tag read (StatusWord), never ICMP.</summary>
    public async Task<PingResult> PingAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            double status = (await ReadAsync(Signals.StatusWord, ct).ConfigureAwait(false))[0];
            return new PingResult(true, FailureReason.None, "OK", sw.Elapsed.TotalMilliseconds, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var (reason, msg) = Classify(ex);
            RecordError(msg);
            return new PingResult(false, reason, msg, double.NaN, null);
        }
    }

    public Task<bool> SendWaveRecipeAsync(WaveCommandPacket p, CancellationToken ct) => TryAsync(async () =>
    {
        await WriteAsync(Signals.PeriodS, [p.PeriodS], ct);
        await WriteAsync(Signals.RampS, [p.RampS], ct);
        await WriteAsync(Signals.Cycles, [p.Cycles], ct);
        await WriteAsync(Signals.AmpMm, p.AmplitudeMm, ct);
        await WriteAsync(Signals.PhaseDeg, p.PhaseDeg, ct);
        await WriteCommandAsync(_cmd & ~CommandBits.ModeTrajectory & ~PulseBits, ct);
    });

    /// <summary>
    /// Handshake: wait BufferLevel ≥ chunk samples → write TrajChunk, ChunkCrc, ChunkSeq → ChunkReady = 1
    /// → wait ChunkAck == Seq. Chunk 1 also writes Cycles = total samples and selects trajectory mode.
    /// </summary>
    public Task<bool> UploadTrajectoryChunkAsync(TrajectoryChunk chunk, CancellationToken ct) => TryAsync(async () =>
    {
        var tag = map.Get(Signals.TrajChunk);
        if (chunk.ValuesMm.Length != tag.Length)
            throw new ArgumentException($"Chunk has {chunk.ValuesMm.Length} values; TrajChunk holds {tag.Length}.");
        int samples = chunk.ValuesMm.Length / map.PaddleCount;

        if (chunk.Seq == 1)
        {
            await WriteAsync(Signals.Cycles, [chunk.TotalSamples], ct);
            await WriteCommandAsync((_cmd | CommandBits.ModeTrajectory) & ~PulseBits, ct);
        }
        if (!await WaitForAsync(Signals.BufferLevel, v => v >= samples, ct))
            throw new PlcProtocolException(FailureReason.Timeout, $"PLC trajectory buffer full (chunk {chunk.Seq}).");

        await WriteAsync(Signals.TrajChunk, Array.ConvertAll(chunk.ValuesMm, v => (double)v), ct);
        await WriteAsync(Signals.ChunkCrc, [chunk.Crc], ct);
        await WriteAsync(Signals.ChunkSeq, [chunk.Seq], ct);
        await WriteAsync(Signals.ChunkReady, [1], ct);

        if (!await WaitForAsync(Signals.ChunkAck, v => (ushort)v == chunk.Seq, ct))
            throw new PlcProtocolException(FailureReason.Timeout, $"Chunk {chunk.Seq} not acknowledged (CRC or sequence rejected?).");
    });

    public Task<bool> SendControlCommandAsync(PlcControlCommand cmd, CancellationToken ct) => TryAsync(() =>
    {
        var level = _cmd & ~PulseBits;
        var next = cmd switch
        {
            PlcControlCommand.Enable => level | CommandBits.Enable,
            PlcControlCommand.Disable => level & ~CommandBits.Enable,
            PlcControlCommand.Home => level | CommandBits.Home,
            PlcControlCommand.Start => level | CommandBits.Start,
            PlcControlCommand.SmoothStop => level | CommandBits.SmoothStop,
            PlcControlCommand.EStop => level | CommandBits.EStop,
            PlcControlCommand.Reset => level | CommandBits.Reset,
            _ => throw new ArgumentOutOfRangeException(nameof(cmd)),
        };
        return WriteCommandAsync(next, ct);
    });

    public async Task SendHeartbeatAsync(ushort value, CancellationToken ct) =>
        await GuardAsync(() => WriteAsync(Signals.HeartbeatPc, [value], ct)).ConfigureAwait(false);

    public Task<TelemetrySnapshot> PollTelemetryAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var pos = await ReadAsync(Signals.PosMm, ct);
        var probes = await ReadAsync(Signals.ProbeM, ct);
        var status = await ReadAsync(Signals.StatusWord, ct);
        var fault = await ReadAsync(Signals.FaultWord, ct);
        var hb = await ReadAsync(Signals.Heartbeat, ct);
        var counter = await ReadAsync(Signals.SampleCounter, ct);
        // Probe_m carries loop current in mA (TagMap scale converts PLC counts to mA); the telemetry
        // service converts it to metres with the probe calibration.
        var probeMa = Array.ConvertAll(probes, v => (float)v);
        return new TelemetrySnapshot(
            _clock.Elapsed.TotalSeconds, (long)counter[0],
            Array.ConvertAll(pos, v => (float)v), probeMa,
            (ushort)status[0], (ushort)fault[0], (ushort)hb[0]) { ProbeRawMa = probeMa };
    });

    /// <summary>Needs a PLC-side sample ring buffer whose layout is still an open input (spec §16).</summary>
    public virtual Task<TelemetryBlock> ReadTelemetryBlockAsync(CancellationToken ct) =>
        throw new NotSupportedException("Block telemetry needs the PLC ring-buffer layout (open input); use poll mode.");

    public async ValueTask DisposeAsync()
    {
        await DisconnectCoreAsync().ConfigureAwait(false);
        _io.Dispose();
        GC.SuppressFinalize(this);
    }

    private const CommandBits PulseBits =
        CommandBits.Home | CommandBits.Start | CommandBits.SmoothStop | CommandBits.EStop | CommandBits.Reset;

    private async Task WriteCommandAsync(CommandBits next, CancellationToken ct)
    {
        await WriteAsync(Signals.CmdWord, [(double)(ushort)next], ct);
        _cmd = next;
    }

    private async Task<bool> WaitForAsync(string signal, Func<double, bool> done, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < Endpoint.TimeoutMs)
        {
            if (done((await ReadAsync(signal, ct))[0])) return true;
            await Task.Delay(HandshakePollMs, ct);
        }
        return false;
    }

    protected async Task<double[]> ReadAsync(string signal, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var values = await ReadTagCoreAsync(map.Get(signal), ct).ConfigureAwait(false);
            RecoverIfFaulted();
            return values;
        }
        finally { _io.Release(); }
    }

    /// <summary>A successful transaction proves the link is up again after an earlier error.</summary>
    private void RecoverIfFaulted()
    {
        if (State == ConnectionState.Fault) SetState(ConnectionState.Connected, "Communication restored");
    }

    protected async Task WriteAsync(string signal, double[] values, CancellationToken ct)
    {
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteTagCoreAsync(map.Get(signal), values, ct).ConfigureAwait(false);
            RecoverIfFaulted();
        }
        finally { _io.Release(); }
    }

    /// <summary>Command-style calls: failures become <c>false</c> plus LastError.</summary>
    private async Task<bool> TryAsync(Func<Task> action)
    {
        try { await action().ConfigureAwait(false); return true; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordError(Classify(ex).Message);
            return false;
        }
    }

    /// <summary>Telemetry-style calls: failures are recorded, the driver faults, and the exception propagates.</summary>
    private async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordError(Classify(ex).Message);
            throw;
        }
    }

    private Task GuardAsync(Func<Task> action) => GuardAsync(async () => { await action(); return 0; });

    protected void RecordError(string message)
    {
        Interlocked.Increment(ref _errors);
        LastError = message;
        SetState(ConnectionState.Fault, message);
    }

    private void SetState(ConnectionState state, string? message)
    {
        if (State == state && message is null) return;
        State = state;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state, message));
    }

    public static (FailureReason Reason, string Message) Classify(Exception ex) => ex switch
    {
        PlcProtocolException p => (p.Reason, p.Message),
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => (FailureReason.Refused, "Connection refused (wrong port, or the PLC service is disabled)."),
        SocketException { SocketErrorCode: SocketError.TimedOut } => (FailureReason.Timeout, "Timeout: no response from the PLC."),
        SocketException { SocketErrorCode: SocketError.ConnectionReset } => (FailureReason.Refused, "Connection reset by the PLC."),
        SocketException s => (FailureReason.ProtocolError, $"Socket error: {s.SocketErrorCode}."),
        TimeoutException or OperationCanceledException => (FailureReason.Timeout, "Timeout: no response from the PLC."),
        IOException { InnerException: SocketException s } => Classify(s),
        IOException => (FailureReason.Timeout, "Timeout or broken connection."),
        _ => (FailureReason.ProtocolError, ex.Message),
    };
}

/// <summary>Base for register/word protocols (Modbus HR, FINS DM, later S7 DB / SLMP D).</summary>
public abstract class WordDriverBase(TagMap map) : TagMapDriverBase(map)
{
    /// <summary>Parses an address string to a word offset, or returns an error.</summary>
    protected abstract (int Start, string? Error) ParseAddress(string address);
    protected abstract int AddressSpaceWords { get; }
    protected abstract Task<ushort[]> ReadWordsAsync(int start, int count, CancellationToken ct);
    protected abstract Task WriteWordsAsync(int start, ushort[] words, CancellationToken ct);

    public override string? CheckAddress(TagEntry tag)
    {
        var (start, err) = ParseAddress(tag.Address);
        if (err is not null) return err;
        return start + WordCodec.WordCount(tag) > AddressSpaceWords ? "address range exceeds the memory area." : null;
    }

    /// <summary>Adds an overlap check: two signals must never share a register.</summary>
    public override IReadOnlyList<string> ValidateTagMap()
    {
        var errors = base.ValidateTagMap().ToList();
        if (errors.Count > 0) return errors;
        var spans = Map.Tags
            .Select(t => (t.Signal, Start: ParseAddress(t.Address).Start, End: ParseAddress(t.Address).Start + WordCodec.WordCount(t)))
            .OrderBy(s => s.Start).ToList();
        for (int i = 1; i < spans.Count; i++)
            if (spans[i].Start < spans[i - 1].End)
                errors.Add($"Signals '{spans[i - 1].Signal}' and '{spans[i].Signal}' overlap.");
        return errors;
    }

    protected override async Task<double[]> ReadTagCoreAsync(TagEntry tag, CancellationToken ct)
    {
        int start = ParseAddress(tag.Address).Start, count = WordCodec.WordCount(tag);
        var words = new ushort[count];
        for (int done = 0; done < count;)
        {
            int n = Math.Min(Capabilities.MaxBlockWords, count - done);
            (await ReadWordsAsync(start + done, n, ct).ConfigureAwait(false)).CopyTo(words, done);
            done += n;
        }
        return WordCodec.Decode(tag, words, Map.WordOrder);
    }

    protected override async Task WriteTagCoreAsync(TagEntry tag, double[] values, CancellationToken ct)
    {
        int start = ParseAddress(tag.Address).Start;
        var words = WordCodec.Encode(tag, values, Map.WordOrder);
        for (int done = 0; done < words.Length;)
        {
            int n = Math.Min(Capabilities.MaxBlockWords, words.Length - done);
            await WriteWordsAsync(start + done, words[done..(done + n)], ct).ConfigureAwait(false);
            done += n;
        }
    }

    /// <summary>Parses "&lt;prefix&gt;&lt;number&gt;", e.g. "D100" or "HR100".</summary>
    protected static (int Start, string? Error) ParsePrefixed(string address, string prefix)
    {
        if (!address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(address.AsSpan(prefix.Length), out int n) || n < 0)
            return (-1, $"address '{address}' must look like {prefix}100.");
        return (n, null);
    }
}
