using FluentAssertions;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

public class WaveStatisticsTests
{
    [Fact]
    public void Regular_sine_gives_exact_heights_and_periods()
    {
        const double fs = 100, a = 0.05, t = 1.7;
        var eta = Enumerable.Range(0, (int)(200 * fs)).Select(i => 0.3 + a * Math.Sin(2 * Math.PI * i / fs / t + 0.4)).ToArray();
        var s = WaveStatistics.ZeroUpcrossing(eta, fs);

        s.WaveCount.Should().BeInRange(115, 118);
        foreach (double h in new[] { s.H13M, s.H10M, s.HmaxM, s.HrmsM })
            h.Should().BeApproximately(2 * a, 2e-4);
        foreach (double p in new[] { s.T13S, s.T10S, s.TmaxS, s.TzS })
            p.Should().BeApproximately(t, 1e-3);
    }

    [Fact]
    public void Skipped_ramps_are_excluded()
    {
        const double fs = 50;
        var eta = Enumerable.Range(0, (int)(60 * fs)).Select(i => i < 10 * fs ? 0.2 * Math.Sin(2 * Math.PI * i / fs) : 0.05 * Math.Sin(2 * Math.PI * i / fs)).ToArray();
        WaveStatistics.ZeroUpcrossing(eta, fs, skipStartS: 11).HmaxM.Should().BeApproximately(0.1, 1e-3);
    }

    [Fact]
    public void Too_short_signal_returns_empty()
    {
        WaveStatistics.ZeroUpcrossing(new double[] { 0, 1, 0 }, 10).WaveCount.Should().Be(0);
    }

    [Fact]
    public void Narrow_band_random_sea_follows_rayleigh()
    {
        // Equal-amplitude components in a 20 % band, seeded phases.
        const double fs = 20, duration = 3000;
        var rng = new Random(42);
        var comps = Enumerable.Range(0, 300).Select(i => (f: 0.45 + 0.1 * i / 299, ph: rng.NextDouble() * 2 * Math.PI)).ToArray();
        const double a = 0.002;
        double sigma = Math.Sqrt(comps.Length * a * a / 2);
        var eta = new double[(int)(duration * fs)];
        for (int i = 0; i < eta.Length; i++)
            foreach (var (f, ph) in comps)
                eta[i] += a * Math.Cos(2 * Math.PI * f * i / fs + ph);

        var s = WaveStatistics.ZeroUpcrossing(eta, fs);
        s.HrmsM.Should().BeApproximately(Math.Sqrt(8) * sigma, 0.06 * Math.Sqrt(8) * sigma);
        (s.H13M / s.HrmsM).Should().BeApproximately(1.416, 0.07);
        (s.H10M / s.HrmsM).Should().BeApproximately(1.80, 0.1);
        s.TzS.Should().BeApproximately(2.0, 0.1);

        var spec = WaveStatistics.Spectral(eta, fs);
        spec.Hm0M.Should().BeApproximately(4 * sigma, 0.02 * 4 * sigma);
        spec.TpS.Should().BeInRange(1.8, 2.25);
    }

    [Fact]
    public void Spectral_m0_equals_variance_of_sine()
    {
        const double fs = 125, a = 0.05;
        double f = 16 * fs / 4096; // exactly on a Welch bin
        var eta = Enumerable.Range(0, 40_000).Select(i => a * Math.Sin(2 * Math.PI * f * i / fs)).ToArray();
        var s = WaveStatistics.Spectral(eta, fs);
        s.M0.Should().BeApproximately(a * a / 2, 0.01 * a * a / 2);
        s.TpS.Should().BeApproximately(1 / f, 1e-9);
    }
}
