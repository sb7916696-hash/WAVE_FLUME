using WaveFlume.Core;

namespace WaveFlume.Physics;

/// <summary>
/// Regular paddle trajectories: X_p(t) = gain · [E(t) Σ X_ai sin(ω_i t + φ_p,i) + E(t)² X2 sin(2ω_1 t + 2φ_p,1)].
/// The optional second-harmonic term (Madsen) is ∝ A², so it ramps with E².
/// </summary>
public static class TrajectoryGenerator
{
    public static Trajectory Generate(
        IReadOnlyList<WaveComponent> components,
        IReadOnlyList<double> paddleY,
        double angleRad,
        double durationS,
        double sampleRateHz,
        double rampS,
        double gain,
        double secondOrderAmpM = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        if (rampS < 0 || rampS > durationS / 2)
            throw new ArgumentOutOfRangeException(nameof(rampS), rampS,
                $"Ramp duration must be in [0, {durationS / 2:0.###}] s (half the signal duration).");

        int n = (int)Math.Round(durationS * sampleRateHz) + 1;
        double dt = 1 / sampleRateHz;
        var envelope = new double[n];
        for (int i = 0; i < n; i++) envelope[i] = gain * Ramp.Envelope(i * dt, durationS, rampS);

        var positions = new float[paddleY.Count][];
        for (int p = 0; p < paddleY.Count; p++)
        {
            var x = new double[n];
            foreach (var c in components)
            {
                double phase = OrientationPhase.PaddlePhase(c.Wave.K, paddleY[p], angleRad);
                double amp = c.StrokeAmplitudeM, w = c.Wave.Omega;
                for (int i = 0; i < n; i++) x[i] += amp * Math.Sin(w * i * dt + phase);
            }
            var row = new float[n];
            for (int i = 0; i < n; i++) row[i] = (float)(envelope[i] * x[i]);
            if (secondOrderAmpM != 0)
            {
                var c = components[0];
                double phase2 = 2 * OrientationPhase.PaddlePhase(c.Wave.K, paddleY[p], angleRad);
                for (int i = 0; i < n; i++)
                {
                    double e = envelope[i] / gain;
                    row[i] += (float)(gain * e * e * secondOrderAmpM * Math.Sin(2 * c.Wave.Omega * i * dt + phase2));
                }
            }
            positions[p] = row;
        }
        return new Trajectory(positions, sampleRateHz);
    }

    /// <summary>
    /// Predicted surface elevation at the paddle (x = 0) for the same components, ramp and gain:
    /// X = X_a sin ωt generates η = (H/2) cos(kx − ωt), so η(0,t) = gain · E(t) · Σ (H_i/2) cos ω_i t.
    /// </summary>
    public static double[] PredictedElevation(
        IReadOnlyList<WaveComponent> components, double durationS, double sampleRateHz, double rampS, double gain)
    {
        int n = (int)Math.Round(durationS * sampleRateHz) + 1;
        var eta = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / sampleRateHz, sum = 0;
            foreach (var c in components) sum += c.HeightM / 2 * Math.Cos(c.Wave.Omega * t);
            eta[i] = gain * Ramp.Envelope(t, durationS, rampS) * sum;
        }
        return eta;
    }
}
