namespace WaveFlume.Physics;

/// <summary>
/// Complete elliptic integrals and the Jacobi cn function by the arithmetic-geometric mean (Abramowitz and Stegun
/// 16.4 and 17.6), with parameter m = k². Needed by cnoidal waves.
/// </summary>
public static class Elliptic
{
    private const double Eps = 1e-16;
    private const int MaxIterations = 40;

    /// <summary>K(m) = ∫₀^{π/2} dθ / √(1 − m sin²θ), for 0 ≤ m &lt; 1.</summary>
    public static double K(double m)
    {
        CheckParameter(m);
        double a = 1, b = Math.Sqrt(1 - m);
        for (int i = 0; i < MaxIterations && Math.Abs(a - b) > Eps * a; i++)
            (a, b) = ((a + b) / 2, Math.Sqrt(a * b));
        return Math.PI / (2 * a);
    }

    /// <summary>E(m) = ∫₀^{π/2} √(1 − m sin²θ) dθ, for 0 ≤ m &lt; 1.</summary>
    public static double E(double m)
    {
        CheckParameter(m);
        double a = 1, b = Math.Sqrt(1 - m), c = Math.Sqrt(m);
        double sum = 0.5 * c * c, power = 0.5;
        for (int i = 0; i < MaxIterations && Math.Abs(c) > Eps; i++)
        {
            double an = (a + b) / 2;
            c = (a - b) / 2;
            b = Math.Sqrt(a * b);
            a = an;
            power *= 2;
            sum += power * c * c;
        }
        return K(m) * (1 - sum);
    }

    /// <summary>Jacobi cn(u | m): equals cos u for m = 0 and sech u for m = 1; period 4K(m).</summary>
    public static double Cn(double u, double m)
    {
        if (m <= 0) return Math.Cos(u);
        if (m >= 1) return 1 / Math.Cosh(u);

        var a = new double[MaxIterations + 1];
        var c = new double[MaxIterations + 1];
        a[0] = 1;
        double b = Math.Sqrt(1 - m);
        c[0] = Math.Sqrt(m);
        int n = 0;
        while (Math.Abs(c[n]) > Eps && n < MaxIterations - 1)
        {
            a[n + 1] = (a[n] + b) / 2;
            c[n + 1] = (a[n] - b) / 2;
            b = Math.Sqrt(a[n] * b);
            n++;
        }
        double phi = Math.Pow(2, n) * a[n] * u;
        for (int i = n; i > 0; i--)
            phi = (phi + Math.Asin(c[i] * Math.Sin(phi) / a[i])) / 2;
        return Math.Cos(phi);
    }

    private static void CheckParameter(double m)
    {
        if (!(m >= 0 && m < 1))
            throw new ArgumentOutOfRangeException(nameof(m), m, "The elliptic parameter must satisfy 0 <= m < 1.");
    }
}
