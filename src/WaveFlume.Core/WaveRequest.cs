namespace WaveFlume.Core;

/// <summary>Operator wave definition. SI units. Defaults match the wave_recipes table.</summary>
public sealed record WaveRequest
{
    public WaveOrder Order { get; init; } = WaveOrder.First;
    public WaveType Type { get; init; } = WaveType.Monochromatic;
    public double WaterDepthM { get; init; } = 0.6;
    public double WaveHeightM { get; init; } = 0.05;
    public double PeriodS { get; init; } = 1.0;
    public int NumWaves { get; init; } = 10;
    public double SamplingHz { get; init; } = 125.0;
    public double SignalModPct { get; init; } = 100.0;
    public double RampS { get; init; } = 2.0;
    public double AngleDeg { get; init; }

    /// <summary>Bichromatic second component.</summary>
    public double? Height2M { get; init; }
    public double? Period2S { get; init; }

    /// <summary>Random waves: height = Hs, period = Tp. Focusing waves use the same spectral shape.</summary>
    public SpectralShape Spectrum { get; init; } = SpectralShape.Jonswap;
    public int RandomSeed { get; init; } = 12345;
    public double Gamma { get; init; } = 3.3;
    public double Beta { get; init; } = 1.25;
    public bool ScaleToTargetHs { get; init; }
    public RandomPath Path { get; init; } = RandomPath.PaddleSpectrum;

    /// <summary>Focusing waves: linear focus at x_f, t_f; height = 2 × focused crest amplitude.</summary>
    public double FocusXM { get; init; } = 10.0;
    public double FocusTimeS { get; init; } = 20.0;

    /// <summary>User-defined wave (η, m) or stroke (X, m) CSV: time_s, value[, value...].</summary>
    public string? UserFilePath { get; init; }
}

public enum SpectralShape { PiersonMoskowitz, GeneralizedPm, Jonswap, Tma, Darbyshire }

/// <summary>Spec §7.6: Path A synthesises the paddle spectrum directly; Path B synthesises η(t) first.</summary>
public enum RandomPath { PaddleSpectrum, SurfaceThenTransfer }

/// <summary>Stored recipe (wave_recipes row). Calculated outputs are a snapshot at save time.</summary>
public sealed record WaveRecipe
{
    public required string TestNumber { get; init; }
    public required WaveRequest Request { get; init; }
    public double? CalcStrokeM { get; init; }
    public double? CalcVelocityMs { get; init; }
    public double? CalcAccelMs2 { get; init; }
    public double? CalcForceKn { get; init; }
    public double? CalcPowerW { get; init; }
    public double? CalcWaveLengthM { get; init; }
    public double? CalcCelerityMs { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Paddle positions, metres, paddle-major: <c>PositionsM[paddle][sample]</c>.</summary>
public sealed record Trajectory(float[][] PositionsM, double SampleRateHz)
{
    public int PaddleCount => PositionsM.Length;
    public int SampleCount => PositionsM.Length == 0 ? 0 : PositionsM[0].Length;
    public double DurationS => SampleCount <= 1 ? 0 : (SampleCount - 1) / SampleRateHz;
}

public sealed record SafetyIssue(string Code, string Message);

public sealed record WorstValues(
    double MaxAbsPositionM, int PositionPaddle,
    double MaxAbsVelocityMs, int VelocityPaddle,
    double MaxAbsAccelMs2, int AccelPaddle);

/// <summary>Gatekeeper verdict. Anything in <see cref="Violations"/> blocks Calculate/Send.</summary>
public sealed record SafetyReport(
    IReadOnlyList<SafetyIssue> Violations,
    IReadOnlyList<SafetyIssue> Warnings,
    WorstValues Worst)
{
    public bool Ok => Violations.Count == 0;
}
