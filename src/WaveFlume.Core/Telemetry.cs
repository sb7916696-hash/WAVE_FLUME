namespace WaveFlume.Core;

/// <summary>
/// One telemetry sample. PLC-boundary units (mm for paddles, m for probes) because it is
/// logged to CSV as-is. <see cref="TimeS"/> is rebuilt from <see cref="SampleCounter"/> in block mode.
/// </summary>
public sealed record TelemetrySnapshot(
    double TimeS,
    long SampleCounter,
    float[] PaddleMm,
    float[] ProbeM,
    ushort StatusWord,
    ushort FaultWord,
    ushort Heartbeat)
{
    /// <summary>
    /// Raw probe current, mA (4–20 mA loop). The telemetry service converts it to <see cref="ProbeM"/>
    /// with the probe calibration and checks it for loop faults. Empty when the source gives metres directly.
    /// </summary>
    public float[] ProbeRawMa { get; init; } = [];
}

/// <summary>Matches telemetry_alerts.alert_severity.</summary>
public enum AlertSeverity { Warning, Critical, EStop }

public sealed record Alert(DateTime UtcTime, AlertSeverity Severity, string Source, string Description);

/// <summary>Matches telemetry_alerts.alert_source.</summary>
public static class AlertSources
{
    public const string MathGatekeeper = "MathGatekeeper";
    public const string PlcStatus = "PLC_Status";
    public const string ServoDrive = "ServoDrive";
    public const string WaveProbe = "WaveProbe";
    public const string Comms = "Comms";
}
