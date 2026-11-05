using CommunityToolkit.Mvvm.ComponentModel;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.App.Services;

/// <summary>
/// Owns the active driver and telemetry loop and derives <see cref="RunState"/> from the PLC StatusWord.
/// <see cref="Refresh"/> is called from the UI timer.
/// </summary>
public sealed partial class PlcSession(AlarmService alarms, AppState app) : ObservableObject, IAsyncDisposable
{
    public IPlcDriver? Driver { get; private set; }
    public PlcTelemetryService? Telemetry { get; private set; }
    public FlumeConfig Flume { get; private set; } = new();
    private PlcEndpoint _endpoint = new();
    private TagMap? _map;

    [ObservableProperty]
    private RunState _state = RunState.Disconnected;

    [ObservableProperty]
    private string _controllerText = "Disconnected";

    [ObservableProperty]
    private string _servoText = "Servo: --";

    [ObservableProperty]
    private ConnectionState _connection = ConnectionState.Disconnected;

    public TelemetrySnapshot? Latest => Telemetry?.UiBuffer.Latest(1) is [var s] ? s : null;

    public async Task<ConnectResult> ConnectAsync(AppSettings settings, TagMap? map, CancellationToken ct)
    {
        await DisconnectAsync();
        Flume = settings.Flume;
        _map = map;
        _endpoint = settings.Connection.ToEndpoint();
        var protocol = settings.Connection.Protocol;
        var driver = DriverFactory.Create(protocol, map, new SimulatorOptions { Flume = settings.Flume });

        ControllerText = $"{protocol} {Target(protocol)}  connecting...";
        Connection = ConnectionState.Connecting;
        var result = await driver.ConnectAsync(_endpoint, ct);
        if (!result.Ok)
        {
            await driver.DisposeAsync();
            Connection = ConnectionState.Fault;
            ControllerText = $"{protocol} {Target(protocol)}  FAULT: {result.Reason}";
            alarms.Raise(AlertSeverity.Warning, AlertSources.Comms, $"Connect failed ({result.Reason}): {result.Message}");
            return result;
        }

        Driver = driver;
        Telemetry = new PlcTelemetryService(driver, new TelemetryOptions
        {
            PollRateHz = settings.Flume.Limits.TelemetryRateHz,
            StrokeClampM = Math.Min(settings.Flume.Limits.MaxStrokeM, HardLimits.MaxStrokeCeilingM),
            ActiveProbes = settings.Flume.Probes.ActiveCount,
            ProbeFaultLowMa = WaveFlume.Data.ProbeCalibration.FaultLowMa,
            ProbeFaultHighMa = WaveFlume.Data.ProbeCalibration.FaultHighMa,
        })
        {
            // Reads the current calibration on every sample, so a saved calibration applies immediately.
            ProbeConverter = raw => app.Calibration.Apply(raw, settings.Flume.Probes),
        };
        Telemetry.AlertRaised += (_, a) => alarms.Raise(a);
        Telemetry.Start(_endpoint);
        Refresh();
        return result;
    }

    public async Task DisconnectAsync()
    {
        if (Telemetry is not null) await Telemetry.StopAsync();
        if (Driver is not null)
        {
            await Driver.DisconnectAsync();
            await Driver.DisposeAsync();
        }
        (Telemetry, Driver) = (null, null);
        Refresh();
    }

    /// <summary>Test Connection: a throw-away driver, so the live session is never disturbed.</summary>
    public static async Task<(ConnectResult Connect, PingResult? Ping)> TestAsync(AppSettings settings, TagMap? map, CancellationToken ct)
    {
        IPlcDriver driver;
        try { driver = DriverFactory.Create(settings.Connection.Protocol, map, new SimulatorOptions { Flume = settings.Flume }); }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentNullException)
        {
            return (ConnectResult.Fail(FailureReason.InvalidConfig, ex.Message), null);
        }
        await using var _ = driver;
        var c = await driver.ConnectAsync(settings.Connection.ToEndpoint(), ct);
        if (!c.Ok) return (c, null);
        var ping = await driver.PingAsync(ct);
        await driver.DisconnectAsync();
        return (c, ping);
    }

    /// <summary>Operator-facing name of a controller command, as shown on the buttons.</summary>
    public static string Friendly(PlcControlCommand cmd) => cmd switch
    {
        PlcControlCommand.Enable => "Enable Servos",
        PlcControlCommand.Disable => "Disable Servos",
        PlcControlCommand.Home => "Starting Position",
        PlcControlCommand.Start => "Start Wave",
        PlcControlCommand.SmoothStop => "Smooth Stop",
        PlcControlCommand.EStop => "Emergency Stop",
        PlcControlCommand.Reset => "Reset Fault",
        _ => cmd.ToString(),
    };

    public async Task<bool> CommandAsync(PlcControlCommand cmd)
    {
        if (Driver is null) return false;
        bool ok = await Driver.SendControlCommandAsync(cmd, CancellationToken.None);
        if (!ok) alarms.Raise(AlertSeverity.Warning, AlertSources.PlcStatus, $"{Friendly(cmd)} refused: {Driver.LastError}");
        Refresh();
        return ok;
    }

    /// <summary>Trajectory mode upload in chunks sized for the driver and TagMap.</summary>
    public async Task<bool> UploadAsync(Trajectory traj, double depthM, IProgress<double> progress, CancellationToken ct)
    {
        if (Driver is null) return false;
        if (Driver is SimulatorDriver sim) sim.WaterDepthM = depthM;
        int tagLength = _map?.Get(Signals.TrajChunk).Length ?? int.MaxValue;
        int perChunk = TrajectoryChunker.ValuesPerChunk(Driver.Capabilities.MaxBlockWords, 2, tagLength, traj.PaddleCount);
        bool ok = await Task.Run(() => TrajectoryChunker.UploadAsync(Driver, traj, perChunk, progress, ct), ct);
        if (!ok) alarms.Raise(AlertSeverity.Critical, AlertSources.PlcStatus, $"Trajectory upload failed: {Driver.LastError}");
        Refresh();
        return ok;
    }

    public void Refresh()
    {
        if (Driver is null)
        {
            State = RunState.Disconnected;
            Connection = Connection == ConnectionState.Fault ? ConnectionState.Fault : ConnectionState.Disconnected;
            if (Connection == ConnectionState.Disconnected) ControllerText = "Disconnected";
            ServoText = "Servo: --";
            return;
        }
        Connection = Driver.State;
        var status = (StatusBits)(Latest?.StatusWord ?? 0);
        State = Driver.State == ConnectionState.Fault
            ? RunState.Faulted
            : StatusMapping.ToRunState(Driver.State == ConnectionState.Connected, status);
        string errors = Driver.CommErrorCount > 0 ? $"  err {Driver.CommErrorCount}" : "";
        ControllerText = Driver.State == ConnectionState.Connected
            ? $"{Driver.Protocol} {Target(Driver.Protocol)}  Connected{errors}"
            : $"{Driver.Protocol} {Target(Driver.Protocol)}  {Driver.State}: {Driver.LastError}{errors}";
        ServoText = status.HasFlag(StatusBits.EStopActive) ? "Servo: E-STOP"
            : status.HasFlag(StatusBits.Enabled) ? (status.HasFlag(StatusBits.Homed) ? "Servo: On, at starting position" : "Servo: On")
            : "Servo: Off";
    }

    private string Target(PlcProtocolType p) =>
        p == PlcProtocolType.Simulator ? "(virtual flume)" : $"{_endpoint.IpAddress}:{_endpoint.Port}";

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
