namespace WaveFlume.Physics;

/// <summary>Half-cosine ramp (spec §7.9). Zero value and zero slope at both ends.</summary>
public static class Ramp
{
    /// <summary>R(t) = ½(1 − cos(π t / T_ramp)) on [0, T_ramp], then 1.</summary>
    public static double Up(double t, double rampS)
    {
        if (rampS <= 0 || t >= rampS) return 1;
        if (t <= 0) return 0;
        return 0.5 * (1 - Math.Cos(Math.PI * t / rampS));
    }

    /// <summary>Ramp-up at the start, mirrored ramp-down ending at <paramref name="durationS"/>.</summary>
    public static double Envelope(double t, double durationS, double rampS) =>
        Up(t, rampS) * Up(durationS - t, rampS);
}
