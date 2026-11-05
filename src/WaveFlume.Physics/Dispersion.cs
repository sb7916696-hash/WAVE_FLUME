namespace WaveFlume.Physics;

/// <summary>Linear wave solved from the dispersion relation (spec §7.1).</summary>
public readonly record struct DispersionResult(double Omega, double K, double DepthM)
{
    public double Kh => K * DepthM;
    public double PeriodS => 2 * Math.PI / Omega;
    public double FrequencyHz => Omega / (2 * Math.PI);
    public double WaveLengthM => 2 * Math.PI / K;
    public double CelerityMs => Omega / K;
    public double GroupFactorN => 0.5 * (1 + 2 * Kh / Math.Sinh(2 * Kh));
    public double GroupVelocityMs => GroupFactorN * CelerityMs;
}

public static class Dispersion
{
    public const int MaxIterations = 15;
    public const double Tolerance = 1e-12;

    /// <summary>Solves ω² = g k tanh(kh) by Newton–Raphson.</summary>
    public static DispersionResult Solve(double periodS, double depthM, double gravity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodS);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depthM);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gravity);

        double omega = 2 * Math.PI / periodS;
        double w2 = omega * omega;
        double k = w2 / (gravity * Math.Sqrt(Math.Tanh(w2 * depthM / gravity)));

        for (int i = 0; i < MaxIterations; i++)
        {
            double kh = k * depthM;
            double tanh = Math.Tanh(kh);
            double sech = 1 / Math.Cosh(kh);
            double f = gravity * k * tanh - w2;
            double df = gravity * tanh + gravity * kh * sech * sech;
            double step = f / df;
            k -= step;
            if (Math.Abs(step) <= Tolerance * k)
                return new DispersionResult(omega, k, depthM);
        }
        throw new InvalidOperationException(
            $"Dispersion solver did not converge in {MaxIterations} iterations (T={periodS} s, h={depthM} m).");
    }
}
