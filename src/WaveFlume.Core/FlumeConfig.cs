using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveFlume.Core;

public sealed record ValueRange(double Min, double Max)
{
    public bool Contains(double value) => value >= Min && value <= Max;
}

/// <summary>Servo → gearbox → rack-and-pinion geometry (spec §3).</summary>
public sealed record DriveConfig
{
    public double GearRatio { get; init; } = 20.0;
    public double PinionModuleMm { get; init; } = 4.0;
    public int PinionTeeth { get; init; } = 25;

    /// <summary>Open input (spec §16). Null until the servo encoder resolution is confirmed.</summary>
    public int? EncoderCountsPerRev { get; init; }

    public double MmPerPinionRev => Math.PI * PinionModuleMm * PinionTeeth;
    public double MmPerMotorRev => MmPerPinionRev / GearRatio;
}

/// <summary>Editable operating limits, SI units. Ceilings in <see cref="HardLimits"/>.</summary>
public sealed record OperationalLimits
{
    /// <summary>Operational ± stroke clamp, m (= 700 mm total).</summary>
    public double MaxStrokeM { get; init; } = 0.35;
    public double MaxVelocityMs { get; init; } = HardLimits.MaxVelocityCeilingMs;

    /// <summary>No default (spec §2): Start/Send is blocked while null.</summary>
    public double? MaxAccelMs2 { get; init; }

    public double TMinS { get; init; } = 0.6;
    public double TMaxS { get; init; } = 6.0;
    public double CommandFsHz { get; init; } = 125.0;
    public double TelemetryRateHz { get; init; } = 125.0;
}

/// <summary>Random/focusing/user-wave synthesis settings (spec §7.6, §7.7).</summary>
public sealed record IrregularWaveSettings
{
    /// <summary>f_lo = max(1/T_max, BandLowFactor · f_p).</summary>
    public double BandLowFactor { get; init; } = 0.4;

    /// <summary>f_hi = min(1/T_min_eff, BandHighFactor · f_p).</summary>
    public double BandHighFactor { get; init; } = 4.0;

    /// <summary>Warn above this many samples per paddle (memory / upload time).</summary>
    public int WarnSamplesPerPaddle { get; init; } = 2_000_000;

}

/// <summary>Wave probes on 4–20 mA loops (spec §3, §10). Count and range are open inputs (§16).</summary>
public sealed record ProbeSettings
{
    /// <summary>Channels actually wired; only these are calibrated and fault-checked.</summary>
    public int ActiveCount { get; init; } = 3;

    /// <summary>Nominal range used until a channel is calibrated: 4 mA ↔ min, 20 mA ↔ max.</summary>
    public double NominalMinM { get; init; } = -0.25;
    public double NominalMaxM { get; init; } = 0.25;

    public double NominalGainMPerMa => (NominalMaxM - NominalMinM) / 16;
    public double NominalOffsetM => NominalMinM - 4 * NominalGainMPerMa;
}

/// <summary>Input range bounds shown in Settings → Range Bounds.</summary>
public sealed record RangeBounds
{
    public ValueRange WaterDepthM { get; init; } = new(0.2, 1.0);
    public ValueRange WaveHeightM { get; init; } = new(0.01, 0.30);
    public ValueRange PeriodS { get; init; } = new(0.5, 6.0);
    public ValueRange NumWaves { get; init; } = new(10, 10_000);
    public ValueRange SamplingHz { get; init; } = new(50, 200);
}

/// <summary>Physical flume description (spec §3). Mirrors config/flume_config.json.</summary>
public sealed record FlumeConfig
{
    public double FlumeWidthM { get; init; } = 3.0;
    public double FlumeLengthM { get; init; } = 45.0;
    public int NumPaddles { get; init; } = 6;
    public double PaddleWidthM { get; init; } = 0.5;
    public double MaxWaterDepthM { get; init; } = 0.8;
    public double Gravity { get; init; } = 9.81;
    public double WaterDensity { get; init; } = 1000.0;
    public PaddleType PaddleType { get; init; } = PaddleType.Piston;

    /// <summary>Hinge height above the flume bed, m. Used only for <see cref="PaddleType.Flap"/>.</summary>
    public double HingeHeightM { get; init; }

    public DriveConfig Drive { get; init; } = new();
    public OperationalLimits Limits { get; init; } = new();
    public RangeBounds Ranges { get; init; } = new();
    public IrregularWaveSettings Irregular { get; init; } = new();
    public ProbeSettings Probes { get; init; } = new();

    /// <summary>Centre of paddle <paramref name="index"/> (0-based) across the flume, m.</summary>
    public double PaddleCentreY(int index) => (index + 0.5) * PaddleWidthM;

    public double[] PaddleCentres() => Enumerable.Range(0, NumPaddles).Select(PaddleCentreY).ToArray();

    /// <summary>max(range_min, T_min, 0.30 s) — spec §2.</summary>
    public double EffectiveTMinS =>
        Math.Max(Math.Max(Ranges.PeriodS.Min, Limits.TMinS), HardLimits.AbsoluteMinPeriodS);

    public double EffectiveTMaxS => Math.Min(Ranges.PeriodS.Max, Limits.TMaxS);

    /// <summary>[min, min(range_max, max_water_depth)] — spec §2.</summary>
    public ValueRange EffectiveDepthRange =>
        new(Ranges.WaterDepthM.Min, Math.Min(Ranges.WaterDepthM.Max, MaxWaterDepthM));

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var l = Limits;
        if (l.MaxStrokeM <= 0 || l.MaxStrokeM > HardLimits.MaxStrokeCeilingM)
            errors.Add($"MaxStrokeM must be in (0, {HardLimits.MaxStrokeCeilingM}] m.");
        if (l.MaxVelocityMs <= 0 || l.MaxVelocityMs > HardLimits.MaxVelocityCeilingMs)
            errors.Add($"MaxVelocityMs must be in (0, {HardLimits.MaxVelocityCeilingMs}] m/s.");
        if (l.MaxAccelMs2 is <= 0)
            errors.Add("MaxAccelMs2 must be positive when set.");
        if (l.TMinS < HardLimits.AbsoluteMinPeriodS)
            errors.Add($"TMinS must be >= {HardLimits.AbsoluteMinPeriodS} s.");
        if (l.TMaxS <= l.TMinS)
            errors.Add("TMaxS must exceed TMinS.");
        if (!Ranges.SamplingHz.Contains(l.CommandFsHz))
            errors.Add("CommandFsHz is outside the sampling-frequency range.");
        if (l.TelemetryRateHz <= 0)
            errors.Add("TelemetryRateHz must be positive.");
        if (NumPaddles <= 0 || PaddleWidthM <= 0)
            errors.Add("NumPaddles and PaddleWidthM must be positive.");
        if (MaxWaterDepthM <= 0 || Gravity <= 0 || WaterDensity <= 0)
            errors.Add("MaxWaterDepthM, Gravity and WaterDensity must be positive.");
        if (Irregular.BandLowFactor <= 0 || Irregular.BandHighFactor <= Irregular.BandLowFactor)
            errors.Add("Irregular band factors must satisfy 0 < low < high.");
        if (HingeHeightM < 0 || (PaddleType == PaddleType.Flap && HingeHeightM >= MaxWaterDepthM))
            errors.Add("HingeHeightM must be in [0, MaxWaterDepthM).");
        foreach (var (name, r) in new[]
                 {
                     ("WaterDepthM", Ranges.WaterDepthM), ("WaveHeightM", Ranges.WaveHeightM),
                     ("PeriodS", Ranges.PeriodS), ("NumWaves", Ranges.NumWaves), ("SamplingHz", Ranges.SamplingHz),
                 })
        {
            if (r.Min <= 0 || r.Max < r.Min) errors.Add($"Range {name} is invalid.");
        }
        return errors;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static FlumeConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<FlumeConfig>(File.ReadAllText(path), JsonOptions)
                  ?? throw new InvalidOperationException($"{path} is empty.");
        var errors = cfg.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException($"{path} is invalid: {string.Join(" ", errors)}");
        return cfg;
    }
}
