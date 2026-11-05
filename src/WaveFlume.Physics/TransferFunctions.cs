using WaveFlume.Core;

namespace WaveFlume.Physics;

/// <summary>Biesel transfer functions c0 = H / S0 (spec §7.2).</summary>
public static class TransferFunctions
{
    /// <summary>
    /// 4 sinh²(kh)/(sinh 2kh + 2kh), evaluated as 2 tanh(kh)/(1 + kh/(sinh kh cosh kh))
    /// so it stays finite (→ 2) where sinh overflows (kh ≳ 355).
    /// </summary>
    public static double Piston(double kh) =>
        2 * Math.Tanh(kh) / (1 + kh / (Math.Sinh(kh) * Math.Cosh(kh)));

    /// <summary>Elevated hinge at height <paramref name="h0"/> above the bed.</summary>
    public static double ElevatedHinge(double k, double h, double h0)
    {
        if (h0 < 0 || h0 >= h)
            throw new ArgumentOutOfRangeException(nameof(h0), h0, "Hinge height must be in [0, h).");
        double kh = k * h;
        // §7.2 numerator and denominator both divided by sinh(kh)·cosh(kh) to avoid overflow;
        // cosh(k h0)/cosh(kh) written with decaying exponentials.
        double coshRatio = Math.Exp(-k * (h - h0)) * (1 + Math.Exp(-2 * k * h0)) / (1 + Math.Exp(-2 * kh));
        double num = 2 * ((h - h0) * k * Math.Tanh(kh) - 1 + coshRatio);
        double den = k * (h - h0) * (1 + kh / (Math.Sinh(kh) * Math.Cosh(kh)));
        return num / den;
    }

    /// <summary>Flap hinged at the bed = elevated hinge with h0 = 0.</summary>
    public static double Flap(double k, double h) => ElevatedHinge(k, h, 0);

    public static double C0(PaddleType type, double k, double h, double hingeHeightM) => type switch
    {
        PaddleType.Piston => Piston(k * h),
        PaddleType.Flap => ElevatedHinge(k, h, hingeHeightM),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
