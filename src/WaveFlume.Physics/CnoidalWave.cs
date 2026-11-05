using WaveFlume.Core;

namespace WaveFlume.Physics;

/// <summary>Solved cnoidal wave: elliptic parameter m, wavelength, speed and trough level (above still water).</summary>
public sealed record CnoidalParameters(
    double M, double K, double E, double LengthM, double CelerityMs, double TroughM, double Ursell);

/// <summary>
/// Cnoidal waves (long waves in shallow water), first order.
///
/// Derivation (Korteweg-de Vries): for a long wave of surface elevation η(x − ct) the KdV equation integrates twice to
/// (c₀h²/12) η'² = −(c₀/4h)(η − η₁)(η − η₂)(η − η₃), with c₀ = √(gh) and roots η₁ (crest) &gt; η₂ (trough) &gt; η₃.
/// Its solution is η = η₂ + H cn²(2K(m)(x − ct)/L | m) with H = η₁ − η₂ and m = (η₁ − η₂)/(η₁ − η₃). Hence
///   L = K(m) √(16 m h³ / (3H)),          Ursell number H L²/h³ = 16 m K(m)²/3,
///   c = c₀ [1 + (η₁ + η₂ + η₃)/(2h)] = c₀ [1 + (H/(m h)) (1 − m/2 − 3E(m)/(2K(m)))],
/// and a mean surface level of zero (mean of cn² = (E/K − 1 + m)/m) gives the trough η₂ = (H/m)(1 − m − E/K).
/// m → 0 is the linear wave and m → 1 the solitary wave (c → √(g(h + H)) to first order). The period T = L/c fixes m.
///
/// Paddle: Goring's long-wave piston rule dX/dt = c η(X, t) / (h + η(X, t)), integrated with RK4, as for the solitary
/// wave. A periodic wave makes the paddle drift slowly, so the linear drift and the mean position are removed and the
/// ramp brings the motion in from the start position.
/// </summary>
public static class CnoidalWave
{
    public const int RkSubsteps = 2;

    /// <summary>Below this Ursell number the long-wave (cnoidal) approximation is poor; the engine warns.</summary>
    public const double UrsellWarnBelow = 10;

    private const double MinM = 0.02;
    private const double MaxOneMinusM = 1e-12;
    private const int ScanPoints = 800;

    public static double Length(double m, double h, double heightM) =>
        Elliptic.K(m) * Math.Sqrt(16 * m * h * h * h / (3 * heightM));

    public static double Celerity(double m, double h, double heightM, double g)
    {
        double ratio = Elliptic.E(m) / Elliptic.K(m);
        return Math.Sqrt(g * h) * (1 + heightM / (m * h) * (1 - m / 2 - 1.5 * ratio));
    }

    public static double Trough(double m, double heightM) => heightM / m * (1 - m - Elliptic.E(m) / Elliptic.K(m));

    private static double Period(double m, double h, double heightM, double g)
    {
        double c = Celerity(m, h, heightM, g);
        // A speed far below the linear one is outside the theory (the wave is too long for this height).
        return c > 0.25 * Math.Sqrt(g * h) ? Length(m, h, heightM) / c : double.NaN;
    }

    /// <summary>Finds m for the requested height, period and depth (the largest m that gives that period).</summary>
    public static CnoidalParameters Solve(double heightM, double periodS, double depthM, double g)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightM);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depthM);

        double step = Math.Log(1 / MaxOneMinusM) / ScanPoints;
        double mHi = 1 - Math.Exp(-ScanPoints * step), tHi = Period(mHi, depthM, heightM, g);
        double tMin = double.PositiveInfinity, tMax = 0;
        double lo = double.NaN, hi = double.NaN;
        for (int i = ScanPoints - 1; i >= 1; i--)
        {
            double mLo = 1 - Math.Exp(-i * step);
            if (mLo < MinM) break;
            double tLo = Period(mLo, depthM, heightM, g);
            if (!double.IsNaN(tLo)) { tMin = Math.Min(tMin, tLo); tMax = Math.Max(tMax, tLo); }
            if (!double.IsNaN(tHi)) { tMin = Math.Min(tMin, tHi); tMax = Math.Max(tMax, tHi); }
            if (!double.IsNaN(tLo) && !double.IsNaN(tHi) && (tLo - periodS) * (tHi - periodS) <= 0)
            {
                lo = mLo;
                hi = mHi;
                break;
            }
            (mHi, tHi) = (mLo, tLo);
        }
        if (double.IsNaN(lo))
            throw new ArgumentException(
                $"A cnoidal wave {heightM:0.###} m high in {depthM:0.###} m of water cannot have a period of {periodS:0.###} s. " +
                (tMax > 0
                    ? $"For this height and depth the period must be between {tMin:0.00} and {tMax:0.00} s. "
                    : "This wave is too high for the water depth. ") +
                "Cnoidal theory is for long waves in shallow water: lower the height, or use a longer period or a shallower depth.");

        for (int i = 0; i < 200; i++)
        {
            double mid = (lo + hi) / 2;
            double t = Period(mid, depthM, heightM, g);
            if (double.IsNaN(t) || (t - periodS) * (Period(lo, depthM, heightM, g) - periodS) > 0) lo = mid; else hi = mid;
            if (hi - lo < 1e-15) break;
        }
        double m = (lo + hi) / 2;
        double k = Elliptic.K(m), e = Elliptic.E(m);
        return new CnoidalParameters(m, k, e, Length(m, depthM, heightM), Celerity(m, depthM, heightM, g),
            Trough(m, heightM), 16 * m * k * k / 3);
    }

    public static GeneratedWave Generate(WaveRequest req, FlumeConfig cfg, double gain)
    {
        double hgt = req.WaveHeightM, d = req.WaterDepthM, period = req.PeriodS, fs = req.SamplingHz;
        var p = Solve(hgt, period, d, cfg.Gravity);
        double duration = req.NumWaves * period;
        if (req.RampS < 0 || req.RampS > duration / 2)
            throw new ArgumentOutOfRangeException(nameof(req), req.RampS,
                $"Ramp duration must be in [0, {duration / 2:0.###}] s (half the signal duration).");

        int n = (int)Math.Round(duration * fs) + 1;
        double argScale = 2 * p.K / p.LengthM;

        double Eta(double x, double t)
        {
            double cn = Elliptic.Cn(argScale * (x - p.CelerityMs * t), p.M);
            return p.TroughM + hgt * cn * cn;
        }
        double Rate(double x, double t)
        {
            double e = Eta(x, t);
            return p.CelerityMs * e / (d + e);
        }

        // Goring: paddle path from dX/dt = c η / (h + η), RK4.
        var raw = new double[n];
        double dt = 1 / fs / RkSubsteps, xi = 0;
        for (int i = 0; i < n; i++)
        {
            raw[i] = xi;
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

        // Remove the slow drift of a periodic wave, then centre the motion on the start position.
        double tLast = (n - 1) / fs, slope = raw[^1] / tLast, mean = 0;
        for (int i = 0; i < n; i++) { raw[i] -= slope * i / fs; mean += raw[i]; }
        mean /= n;

        var x = new double[n];
        var eta = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / fs, env = gain * Ramp.Envelope(t, duration, req.RampS);
            x[i] = env * (raw[i] - mean);
            eta[i] = env * Eta(0, t);
        }

        var row = Array.ConvertAll(x, v => (float)v);
        var rows = Enumerable.Range(0, cfg.NumPaddles).Select(_ => (float[])row.Clone()).ToArray();
        var warnings = new List<SafetyIssue>();
        if (p.Ursell < UrsellWarnBelow)
            warnings.Add(new("CNOIDAL_RANGE",
                $"Ursell number {p.Ursell:0.#} is below {UrsellWarnBelow}: the wave is not long enough compared with the depth for cnoidal theory to be accurate."));

        return new GeneratedWave(new Trajectory(rows, fs), eta, [WaveComponent.Create(hgt, period, d, cfg)], [])
        {
            Warnings = warnings,
            Info =
            [
                $"Cnoidal (first order): m = {p.M:0.000000}, L = {p.LengthM:0.00} m, c = {p.CelerityMs:0.000} m/s, trough {p.TroughM * 1000:0.0} mm, Ursell {p.Ursell:0.#}.",
                $"Paddle by Goring's long-wave rule; drift of {slope * 1000:0.00} mm/s removed so the paddle stays about its start position.",
            ],
        };
    }
}
