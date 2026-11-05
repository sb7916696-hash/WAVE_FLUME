using WaveFlume.Core;

namespace WaveFlume.Physics;

public sealed record SpectrumSpec(
    SpectralShape Shape, double HsM, double TpS,
    double Gamma = 3.3, double Beta = 1.25, double DepthM = double.NaN, double Gravity = 9.81)
{
    public double Fp => 1 / TpS;
}

/// <summary>
/// Spectral shapes (spec §7.7, f_p = 1/T_p). Every shape is generated and then scaled so that
/// m0 = ∫S df = Hs²/16 on a fine trapezoid grid.
/// </summary>
public static class Spectra
{
    public const double PmBeta = 1.25;
    public const double JonswapSigmaLow = 0.07;
    public const double JonswapSigmaHigh = 0.09;

    /// <summary>Normalisation grid: (0, UpperFactor · f_p], fine enough for &lt; 1e-4 relative error.</summary>
    public const double NormUpperFactor = 20;
    public const int NormPoints = 40_000;

    /// <summary>S = (5/16) Hs² fp⁴ f⁻⁵ exp[−(5/4)(f/fp)⁻⁴].</summary>
    public static double PiersonMoskowitz(double f, double hs, double fp) => GeneralizedPm(f, hs, fp, PmBeta);

    /// <summary>S = (β/4) Hs² fp⁴ f⁻⁵ exp[−β (f/fp)⁻⁴] — integrates to Hs²/16 for every β.</summary>
    public static double GeneralizedPm(double f, double hs, double fp, double beta) =>
        f <= 0 ? 0 : beta / 4 * hs * hs * Math.Pow(fp, 4) * Math.Pow(f, -5) * Math.Exp(-beta * Math.Pow(f / fp, -4));

    /// <summary>JONSWAP shape (unnormalised): f⁻⁵ exp[−(5/4)(f/fp)⁻⁴] γ^exp[−½((f−fp)/(σ fp))²].</summary>
    public static double JonswapShape(double f, double fp, double gamma)
    {
        if (f <= 0) return 0;
        double sigma = f <= fp ? JonswapSigmaLow : JonswapSigmaHigh;
        double r = Math.Exp(-0.5 * Math.Pow((f - fp) / (sigma * fp), 2));
        return Math.Pow(f, -5) * Math.Exp(-1.25 * Math.Pow(f / fp, -4)) * Math.Pow(gamma, r);
    }

    /// <summary>TMA depth factor φ(ω_h), ω_h = 2πf√(h/g).</summary>
    public static double TmaFactor(double f, double depthM, double gravity)
    {
        double wh = 2 * Math.PI * f * Math.Sqrt(depthM / gravity);
        return wh <= 1 ? 0.5 * wh * wh : wh <= 2 ? 1 - 0.5 * Math.Pow(2 - wh, 2) : 1;
    }

    /// <summary>
    /// Darbyshire spectrum, IITM WaveGen theory manual eq. (9): 1.429 Hs² exp[−((f−fp)² / (0.00847 (f−fp+0.042)))^½] for f − fp &gt; −0.042 Hz.
    /// Like every shape it is renormalised so that m0 = Hs²/16.
    /// </summary>
    public static double DarbyshireShape(double f, double hs, double fp)
    {
        double d = f - fp;
        if (d <= -0.042) return 0;
        return 1.429 * hs * hs * Math.Exp(-Math.Sqrt(d * d / (0.00847 * (d + 0.042))));
    }

    /// <summary>Unnormalised shape value.</summary>
    public static double Shape(SpectrumSpec s, double f) => s.Shape switch
    {
        SpectralShape.PiersonMoskowitz => PiersonMoskowitz(f, s.HsM, s.Fp),
        SpectralShape.GeneralizedPm => GeneralizedPm(f, s.HsM, s.Fp, s.Beta),
        SpectralShape.Jonswap => JonswapShape(f, s.Fp, s.Gamma),
        SpectralShape.Tma => TmaFactor(f, Depth(s), s.Gravity) * JonswapShape(f, s.Fp, s.Gamma),
        SpectralShape.Darbyshire => DarbyshireShape(f, s.HsM, s.Fp),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    /// <summary>S(f) scaled so that m0 = Hs²/16.</summary>
    public static Func<double, double> Normalised(SpectrumSpec s)
    {
        double m0 = Integrate(f => Shape(s, f), 0, NormUpperFactor * s.Fp, NormPoints);
        if (!(m0 > 0)) throw new InvalidOperationException($"Spectrum {s.Shape} has no energy.");
        double scale = s.HsM * s.HsM / 16 / m0;
        return f => scale * Shape(s, f);
    }

    public static double Integrate(Func<double, double> g, double f0, double f1, int points)
    {
        double df = (f1 - f0) / (points - 1), sum = 0;
        for (int i = 0; i < points; i++)
        {
            double w = i == 0 || i == points - 1 ? 0.5 : 1;
            sum += w * g(f0 + i * df);
        }
        return sum * df;
    }

    private static double Depth(SpectrumSpec s) =>
        double.IsNaN(s.DepthM) ? throw new ArgumentException("TMA needs the water depth.") : s.DepthM;
}
