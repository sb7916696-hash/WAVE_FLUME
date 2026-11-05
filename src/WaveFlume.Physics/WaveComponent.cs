using WaveFlume.Core;

namespace WaveFlume.Physics;

/// <summary>One linear wave component and its first-order paddle amplitude (spec §7.3).</summary>
public sealed record WaveComponent(double HeightM, DispersionResult Wave, double C0)
{
    /// <summary>X_a = H / (2 c0): half-stroke ("Stroke amplitude").</summary>
    public double StrokeAmplitudeM => HeightM / (2 * C0);

    public static WaveComponent Create(double heightM, double periodS, double depthM, FlumeConfig cfg)
    {
        var wave = Dispersion.Solve(periodS, depthM, cfg.Gravity);
        return new WaveComponent(heightM, wave, TransferFunctions.C0(cfg.PaddleType, wave.K, depthM, cfg.HingeHeightM));
    }
}

/// <summary>Oblique-wave phase for a segmented wavemaker (spec §7.8).</summary>
public static class OrientationPhase
{
    /// <summary>φ = −k · y_p · sin θ.</summary>
    public static double PaddlePhase(double k, double paddleY, double angleRad) => -k * paddleY * Math.Sin(angleRad);

    /// <summary>|Δφ| = k · b · |sin θ| between adjacent paddles.</summary>
    public static double AdjacentStep(double k, double paddleWidth, double angleRad) =>
        k * paddleWidth * Math.Abs(Math.Sin(angleRad));
}
