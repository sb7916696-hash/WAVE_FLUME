namespace WaveFlume.Core;

/// <summary>
/// Absolute ceilings (spec §2). These are deliberately compile-time constants:
/// no configuration file, database row or setting can raise them.
/// </summary>
public static class HardLimits
{
    /// <summary>Software stroke ceiling, ± metres. Operational clamp may only be lower.</summary>
    public const double MaxStrokeCeilingM = 0.45;

    /// <summary>Velocity ceiling, m/s.</summary>
    public const double MaxVelocityCeilingMs = 1.57;

    /// <summary>Absolute period floor, s.</summary>
    public const double AbsoluteMinPeriodS = 0.30;

    /// <summary>Mechanical travel, ± metres. Informational only — never used for checks.</summary>
    public const double HardwareTravelM = 0.50;
}
