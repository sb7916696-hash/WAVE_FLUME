using WaveFlume.Core;

namespace WaveFlume.Plc;

public enum PlcProtocolType { Simulator, ModbusTcp, OpcUa, OmronFins, EtherNetIp, S7, Slmp, Ads }

public enum PlcTransport { Tcp, Udp }

public enum ConnectionState { Disconnected, Connecting, Connected, Fault }

public enum FailureReason { None, Timeout, Refused, WrongNode, AccessDenied, InvalidConfig, ProtocolError }

public enum PlcControlCommand { Enable, Disable, Home, Start, SmoothStop, EStop, Reset }

/// <summary>CmdWord bits (PC → PLC). Level bits: Enable, Mode. Pulse bits: the PLC acts on the rising edge.</summary>
[Flags]
public enum CommandBits : ushort
{
    None = 0,
    Enable = 1 << 0,
    Home = 1 << 1,
    Start = 1 << 2,
    SmoothStop = 1 << 3,
    /// <summary>Additional to the hardwired E-stop chain, never a replacement for it.</summary>
    EStop = 1 << 4,
    Reset = 1 << 5,
    /// <summary>0 = parametric, 1 = trajectory.</summary>
    ModeTrajectory = 1 << 6,
}

/// <summary>StatusWord bits (PLC → PC).</summary>
[Flags]
public enum StatusBits : ushort
{
    None = 0,
    Enabled = 1 << 0,
    Homed = 1 << 1,
    Running = 1 << 2,
    TrajectoryLoaded = 1 << 3,
    Fault = 1 << 4,
    EStopActive = 1 << 5,
    SmoothStopping = 1 << 6,
}

/// <summary>FaultWord bits (PLC → PC). <see cref="HardFaultMask"/> bits demand an E-stop.</summary>
[Flags]
public enum FaultBits : ushort
{
    None = 0,
    CommsLoss = 1 << 0,
    StrokeLimit = 1 << 1,
    VelocityLimit = 1 << 2,
    AccelLimit = 1 << 3,
    FollowingError = 1 << 4,
    DriveFault = 1 << 5,
    ChunkError = 1 << 6,
    HardFaultMask = DriveFault | StrokeLimit,
}

public sealed record DriverCapabilities(int MaxBlockWords, bool SupportsFloat32, bool SupportsBlockTelemetry);

/// <summary>Where and how to reach the controller. <see cref="Extras"/> holds protocol-specific keys.</summary>
public sealed record PlcEndpoint
{
    public string IpAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; }
    public PlcTransport Transport { get; init; } = PlcTransport.Tcp;
    public int TimeoutMs { get; init; } = 1000;
    public int Retries { get; init; } = 3;
    public Dictionary<string, string> Extras { get; init; } = [];

    public string? Extra(string key) => Extras.TryGetValue(key, out var v) ? v : null;
    public int ExtraInt(string key, int fallback) => int.TryParse(Extra(key), out var v) ? v : fallback;
}

public static class EndpointExtras
{
    public const string ModbusUnitId = "UnitId";
    public const string FinsDestNetwork = "FinsDestNetwork";
    public const string FinsDestNode = "FinsDestNode";
    public const string FinsDestUnit = "FinsDestUnit";
    public const string FinsSourceNode = "FinsSourceNode";
    public const string OpcUaSecurity = "OpcUaSecurity";
    public const string OpcUaUser = "OpcUaUser";
    public const string OpcUaPassword = "OpcUaPassword";
}

public sealed record ConnectResult(bool Ok, FailureReason Reason, string Message, double RttMs)
{
    public static ConnectResult Success(double rttMs) => new(true, FailureReason.None, "Connected", rttMs);
    public static ConnectResult Fail(FailureReason reason, string message) => new(false, reason, message, double.NaN);
}

public sealed record PingResult(bool Ok, FailureReason Reason, string Message, double RttMs, double? TestTagValue);

public sealed record ConnectionStateChangedEventArgs(ConnectionState State, string? Message);

/// <summary>Parametric mode: the PLC generates the sine (spec §9.2).</summary>
public sealed record WaveCommandPacket(double PeriodS, double RampS, int Cycles, double[] AmplitudeMm, double[] PhaseDeg);

/// <summary>
/// Trajectory mode: interleaved positions, mm — sample-major, <c>[s0p1..s0p6, s1p1..]</c>.
/// Every chunk is full length (the last one repeats its final sample); <see cref="TotalSamples"/>
/// tells the PLC where the trajectory really ends and is written to <c>Cycles</c> with chunk 1.
/// <see cref="Crc"/> is CRC-16/MODBUS over the values as IEEE-754 big-endian bytes.
/// </summary>
public sealed record TrajectoryChunk(ushort Seq, float[] ValuesMm, ushort Crc, int TotalSamples);

public sealed record TelemetryBlock(IReadOnlyList<TelemetrySnapshot> Samples, long LostSamples);

/// <summary>Vendor-agnostic controller access (spec §9.1).</summary>
public interface IPlcDriver : IAsyncDisposable
{
    PlcProtocolType Protocol { get; }
    DriverCapabilities Capabilities { get; }
    ConnectionState State { get; }
    long CommErrorCount { get; }
    string? LastError { get; }
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    Task<ConnectResult> ConnectAsync(PlcEndpoint endpoint, CancellationToken ct);
    Task DisconnectAsync();
    Task<PingResult> PingAsync(CancellationToken ct);
    Task<bool> SendWaveRecipeAsync(WaveCommandPacket packet, CancellationToken ct);
    Task<bool> UploadTrajectoryChunkAsync(TrajectoryChunk chunk, CancellationToken ct);
    Task<bool> SendControlCommandAsync(PlcControlCommand cmd, CancellationToken ct);

    /// <summary>PC → PLC heartbeat (spec §9.4: toggled every cycle, both directions).</summary>
    Task SendHeartbeatAsync(ushort value, CancellationToken ct);

    Task<TelemetrySnapshot> PollTelemetryAsync(CancellationToken ct);
    Task<TelemetryBlock> ReadTelemetryBlockAsync(CancellationToken ct);
}
