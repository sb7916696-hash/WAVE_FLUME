using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace WaveFlume.Physics;

public sealed record ZeroCrossingStats(
    int WaveCount,
    double H13M, double H10M, double HmaxM, double HrmsM,
    double T13S, double T10S, double TmaxS, double TzS);

public sealed record SpectralStats(double M0, double Hm0M, double TpS, double[] FrequencyHz, double[] Psd);

/// <summary>Zero-upcrossing and spectral wave statistics (spec §7.12).</summary>
public static class WaveStatistics
{
    public const int DefaultWelchSegment = 4096;

    /// <summary>
    /// Mean is removed, <paramref name="skipStartS"/>/<paramref name="skipEndS"/> discard ramps.
    /// Incomplete waves before the first and after the last up-crossing are dropped.
    /// </summary>
    public static ZeroCrossingStats ZeroUpcrossing(
        ReadOnlySpan<double> eta, double sampleRateHz, double skipStartS = 0, double skipEndS = 0)
    {
        int i0 = (int)Math.Round(skipStartS * sampleRateHz);
        int i1 = eta.Length - (int)Math.Round(skipEndS * sampleRateHz);
        if (i1 - i0 < 3)
            return Empty();
        var x = eta[i0..i1].ToArray();
        double mean = x.Average();
        for (int i = 0; i < x.Length; i++) x[i] -= mean;

        // Up-crossing instants in samples, linearly interpolated.
        var crossings = new List<(int Index, double T)>();
        for (int i = 0; i < x.Length - 1; i++)
            if (x[i] < 0 && x[i + 1] >= 0)
                crossings.Add((i, i + x[i] / (x[i] - x[i + 1])));
        if (crossings.Count < 2)
            return Empty();

        var waves = new List<(double H, double T)>(crossings.Count - 1);
        for (int c = 0; c < crossings.Count - 1; c++)
        {
            double max = double.MinValue, min = double.MaxValue;
            for (int i = crossings[c].Index; i <= crossings[c + 1].Index + 1; i++)
            {
                max = Math.Max(max, x[i]);
                min = Math.Min(min, x[i]);
            }
            waves.Add((max - min, (crossings[c + 1].T - crossings[c].T) / sampleRateHz));
        }

        var sorted = waves.OrderByDescending(w => w.H).ToList();
        (double H, double T) MeanOfTop(int fraction)
        {
            var top = sorted.Take(Math.Max(1, sorted.Count / fraction)).ToList();
            return (top.Average(w => w.H), top.Average(w => w.T));
        }
        var (h13, t13) = MeanOfTop(3);
        var (h10, t10) = MeanOfTop(10);
        return new ZeroCrossingStats(
            waves.Count, h13, h10, sorted[0].H, Math.Sqrt(waves.Average(w => w.H * w.H)),
            t13, t10, sorted[0].T, waves.Average(w => w.T));
    }

    /// <summary>Welch PSD (Hann window, 50 % overlap), one-sided; m0, Hm0 = 4√m0, Tp.</summary>
    public static SpectralStats Spectral(ReadOnlySpan<double> eta, double sampleRateHz, int segmentLength = DefaultWelchSegment)
    {
        int n = Math.Min(segmentLength, eta.Length);
        if (n < 4)
            throw new ArgumentException("Signal too short for spectral analysis.", nameof(eta));
        var window = new double[n];
        double u = 0;
        for (int i = 0; i < n; i++)
        {
            window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (n - 1)));
            u += window[i] * window[i];
        }

        int bins = n / 2 + 1;
        var psd = new double[bins];
        int segments = 0;
        var buf = new Complex[n];
        for (int start = 0; start + n <= eta.Length; start += n / 2, segments++)
        {
            var seg = eta.Slice(start, n);
            double mean = 0;
            foreach (var s in seg) mean += s;
            mean /= n;
            for (int i = 0; i < n; i++) buf[i] = new Complex((seg[i] - mean) * window[i], 0);
            Fourier.Forward(buf, FourierOptions.Matlab);
            for (int k = 0; k < bins; k++)
            {
                double p = buf[k].Magnitude * buf[k].Magnitude / (sampleRateHz * u);
                bool edge = k == 0 || (n % 2 == 0 && k == n / 2);
                psd[k] += edge ? p : 2 * p;
            }
        }

        double df = sampleRateHz / n;
        var freq = new double[bins];
        double m0 = 0;
        int peak = 1;
        for (int k = 0; k < bins; k++)
        {
            psd[k] /= segments;
            freq[k] = k * df;
            m0 += psd[k] * df;
            if (k > 0 && psd[k] > psd[peak]) peak = k;
        }
        return new SpectralStats(m0, 4 * Math.Sqrt(m0), 1 / freq[peak], freq, psd);
    }

    private static ZeroCrossingStats Empty() =>
        new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
}
