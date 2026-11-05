using WaveFlume.Core;

namespace WaveFlume.Physics;

/// <summary>
/// Solitary wave, Goring (1978) (spec §7.5): η = H sech²(κ(x − c t)), κ = √(3H/4h³), c = √(g(h+H)),
/// paddle path from dX/dt = c·η(X,t)/(h + η(X,t)) integrated with RK4.
/// </summary>
public static class SolitaryWave
{
    /// <summary>
    /// The crest is placed so that η at the paddle starts (and ends) at this fraction of H.
    /// Numerical choice, not hydrodynamics: it sets the motion duration.
    /// </summary>
    public const double EdgeFraction = 1e-3;

    public const int RkSubsteps = 4;

    public static double Kappa(double h, double depth) => Math.Sqrt(3 * h / (4 * depth * depth * depth));
    public static double Celerity(double h, double depth, double g) => Math.Sqrt(g * (depth + h));

    public static GeneratedWave Generate(WaveRequest req, FlumeConfig cfg, double gain)
    {
        double hgt = req.WaveHeightM, d = req.WaterDepthM, g = cfg.Gravity, fs = req.SamplingHz;
        double kappa = Kappa(hgt, d), c = Celerity(hgt, d, g);
        double tc = Math.Acosh(1 / Math.Sqrt(EdgeFraction)) / (kappa * c); // crest passes x = 0 at t = tc
        int n = (int)Math.Ceiling(2 * tc * fs) + 1;

        double Eta(double x, double t)
        {
            double s = 1 / Math.Cosh(kappa * (x - c * (t - tc)));
            return hgt * s * s;
        }
        double Rate(double x, double t)
        {
            double e = Eta(x, t);
            return c * e / (d + e);
        }

        var x = new double[n];
        var eta = new double[n];
        double dt = 1 / fs / RkSubsteps, xi = 0;
        for (int i = 0; i < n; i++)
        {
            x[i] = xi;
            eta[i] = Eta(xi, i / fs);
            for (int s = 0; s < RkSubsteps; s++)
            {
                double t = i / fs + s * dt;
                double k1 = Rate(xi, t);
                double k2 = Rate(xi + dt / 2 * k1, t + dt / 2);
                double k3 = Rate(xi + dt / 2 * k2, t + dt / 2);
                double k4 = Rate(xi + dt * k3, t + dt);
                xi += dt / 6 * (k1 + 2 * k2 + 2 * k3 + k4);
            }
        }

        var row = Array.ConvertAll(x, v => (float)(v * gain));
        var rows = Enumerable.Range(0, cfg.NumPaddles).Select(_ => (float[])row.Clone()).ToArray();
        return new GeneratedWave(new Trajectory(rows, fs), Array.ConvertAll(eta, e => e * gain), [], [])
        {
            Heights = [hgt],
            UsesNumWaves = false,
            Info =
            [
                $"Solitary (Goring): H = {hgt} m, κ = {kappa:0.000} 1/m, c = {c:0.000} m/s, duration {(n - 1) / fs:0.00} s, total stroke {x[^1] * gain:0.0000} m (paddle ends displaced; home afterwards).",
            ],
        };
    }
}

/// <summary>Second-order wavemaker theory (spec §7.4).</summary>
public static class SecondOrder
{
    /// <summary>
    /// [VERIFY] Madsen (1971), piston, monochromatic: X0⁽²⁾(t) = (H²/(32h))·(3 cosh kh / sinh³ kh − 2/c0)·sin 2ωt.
    /// Returns the coefficient of sin 2ωt. Sign/phase still to be checked against manual eq. 3 with n = m.
    /// </summary>
    public static double MadsenPistonAmplitude(double heightM, double depthM, double kh, double c0)
    {
        double sh = Math.Sinh(kh);
        return heightM * heightM / (32 * depthM) * (3 * Math.Cosh(kh) / (sh * sh * sh) - 2 / c0);
    }

    /// <summary>Stokes second-order bound harmonic: η₂ = (H²k/16)·cosh kh (2 + cosh 2kh)/sinh³ kh · cos 2θ.</summary>
    public static double StokesBoundAmplitude(double heightM, double k, double kh)
    {
        double sh = Math.Sinh(kh);
        return heightM * heightM * k / 16 * Math.Cosh(kh) * (2 + Math.Cosh(2 * kh)) / (sh * sh * sh);
    }

    /// <summary>General F±/G± (Schäffer 1996) are not implemented: the spec forbids approximating them from memory.</summary>
    public static NotImplementedException SchaefferUnavailable(WaveType type) => new(
        $"2nd order for {type} needs Schäffer (1996) F±/G± transfer functions, which are not yet provided/verified. " +
        "Use 1st order, or 2nd order Monochromatic (Madsen).");
}
