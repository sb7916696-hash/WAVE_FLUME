using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using WaveFlume.Core;

namespace WaveFlume.Physics;

/// <summary>
/// Frequency-domain synthesis on an FFT grid of length L (a power of two, ≥ the record), bin j at f_j = j·fs/L.
/// Surface convention: η(0,t) = Σ Re(Z_j e^{iω_j t}). A linear paddle component X = X_a sin(ωt − ε) makes
/// η = c0 X_a cos(ωt − ε), so the paddle bin is B_j = −i Z_j / c0_j, times e^{−i k_j y_p sin θ} for oblique waves.
/// </summary>
public static class SpectralSynthesis
{
    /// <summary>
    /// ponytail: power-of-two FFT keeps MathNet on its radix-2 path (arbitrary N falls back to Bluestein,
    /// ~3×2N complex buffers). The synthesis repeat period becomes L/fs ≥ the record length.
    /// </summary>
    public static int FftLength(int minSamples)
    {
        int n = 2;
        while (n < minSamples) n <<= 1;
        return n;
    }

    /// <summary>Wave number and transfer function for every bin that carries energy.</summary>
    public sealed record BinTransfer(double[] K, double[] C0);

    public static BinTransfer Transfer(Complex[] z, int fftLength, double fs, double depthM, FlumeConfig cfg)
    {
        var k = new double[z.Length];
        var c0 = new double[z.Length];
        for (int j = 1; j < z.Length; j++)
        {
            if (z[j] == Complex.Zero) continue;
            var w = Dispersion.Solve(fftLength / (j * fs), depthM, cfg.Gravity);
            k[j] = w.K;
            c0[j] = TransferFunctions.C0(cfg.PaddleType, w.K, depthM, cfg.HingeHeightM);
        }
        return new BinTransfer(k, c0);
    }

    /// <summary>η(0,t) for the first <paramref name="outSamples"/> samples.</summary>
    public static double[] Surface(Complex[] z, int fftLength, int outSamples)
    {
        var buf = new Complex[fftLength];
        Fill(buf, z, j => z[j]);
        return Real(buf, outSamples, null);
    }

    /// <summary>Half-spectrum Z_j of a real series (zero-padded to <paramref name="fftLength"/>).</summary>
    public static Complex[] SurfaceBins(ReadOnlySpan<double> eta, int fftLength)
    {
        var buf = new Complex[fftLength];
        for (int i = 0; i < eta.Length; i++) buf[i] = eta[i];
        Fourier.Forward(buf, FourierOptions.NoScaling);
        var z = new Complex[fftLength / 2 + 1];
        for (int j = 1; j < fftLength / 2; j++) z[j] = 2.0 / fftLength * buf[j];
        return z;
    }

    /// <summary>X_p(t) for every paddle, multiplied sample-wise by <paramref name="envelope"/> (ramp × gain).</summary>
    public static float[][] Paddles(Complex[] z, BinTransfer t, int fftLength, IReadOnlyList<double> paddleY,
        double angleRad, double[] envelope)
    {
        double sinTheta = Math.Sin(angleRad);
        var buf = new Complex[fftLength];
        var rows = new float[paddleY.Count][];
        for (int p = 0; p < paddleY.Count; p++)
        {
            double y = paddleY[p];
            Array.Clear(buf);
            Fill(buf, z, j => -Complex.ImaginaryOne * z[j] / t.C0[j] * Complex.FromPolarCoordinates(1, -t.K[j] * y * sinTheta));
            var x = Real(buf, envelope.Length, envelope);
            rows[p] = Array.ConvertAll(x, v => (float)v);
        }
        return rows;
    }

    /// <summary>Inverse of the paddle transfer: η(0,t) produced by a paddle motion, bins limited to [fLo, fHi].</summary>
    public static double[] SurfaceFromPaddle(ReadOnlySpan<double> x, double fs, double depthM, FlumeConfig cfg, double fLo, double fHi)
    {
        int length = FftLength(2 * x.Length);
        var b = SurfaceBins(x, length);
        LimitBand(b, length, fs, fLo, fHi);
        var t = Transfer(b, length, fs, depthM, cfg);
        var z = new Complex[b.Length];
        for (int j = 1; j < b.Length; j++) z[j] = Complex.ImaginaryOne * t.C0[j] * b[j];
        return Surface(z, length, x.Length);
    }

    /// <summary>Zeros bins outside [fLo, fHi]; returns the fraction of Σ|Z|² removed.</summary>
    public static double LimitBand(Complex[] z, int fftLength, double fs, double fLo, double fHi)
    {
        double total = 0, removed = 0;
        for (int j = 1; j < z.Length; j++)
        {
            double e = z[j].Magnitude * z[j].Magnitude;
            total += e;
            double f = j * fs / fftLength;
            if (f >= fLo && f <= fHi) continue;
            removed += e;
            z[j] = Complex.Zero;
        }
        return total > 0 ? removed / total : 0;
    }

    private static void Fill(Complex[] buf, Complex[] z, Func<int, Complex> bin)
    {
        int length = buf.Length;
        for (int j = 1; j < length / 2 && j < z.Length; j++)
        {
            if (z[j] == Complex.Zero) continue;
            var b = bin(j) / 2;
            buf[j] = b;
            buf[length - j] = Complex.Conjugate(b);
        }
    }

    private static double[] Real(Complex[] buf, int outSamples, double[]? envelope)
    {
        Fourier.Inverse(buf, FourierOptions.NoScaling);
        var x = new double[outSamples];
        for (int i = 0; i < outSamples; i++) x[i] = buf[i].Real * (envelope?[i] ?? 1);
        return x;
    }
}
