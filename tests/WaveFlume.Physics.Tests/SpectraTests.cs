using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

/// <summary>Spec §14 spectra checks.</summary>
public class SpectraTests
{
    private const double Hs = 0.1, Tp = 1.5, Fp = 1 / Tp;
    private static double M0 => Hs * Hs / 16;
    private static double Integral(Func<double, double> s) => Spectra.Integrate(s, 0, Spectra.NormUpperFactor * Fp, Spectra.NormPoints);

    public static IEnumerable<object[]> Shapes() =>
    [
        [new SpectrumSpec(SpectralShape.PiersonMoskowitz, Hs, Tp)],
        [new SpectrumSpec(SpectralShape.GeneralizedPm, Hs, Tp, Beta: 0.8)],
        [new SpectrumSpec(SpectralShape.GeneralizedPm, Hs, Tp, Beta: 2.0)],
        [new SpectrumSpec(SpectralShape.Jonswap, Hs, Tp, Gamma: 1)],
        [new SpectrumSpec(SpectralShape.Jonswap, Hs, Tp, Gamma: 3.3)],
        [new SpectrumSpec(SpectralShape.Jonswap, Hs, Tp, Gamma: 7)],
        [new SpectrumSpec(SpectralShape.Tma, Hs, Tp, DepthM: 0.3)],
        [new SpectrumSpec(SpectralShape.Darbyshire, Hs, Tp)],
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_shape_integrates_to_hs_squared_over_16(SpectrumSpec spec) =>
        Integral(Spectra.Normalised(spec)).Should().BeApproximately(M0, 1e-6 * M0);

    [Theory]
    [InlineData(0.8)]
    [InlineData(1.25)]
    [InlineData(2.0)]
    public void Generalized_pm_closed_form_is_already_normalised(double beta) =>
        Integral(f => Spectra.GeneralizedPm(f, Hs, Fp, beta)).Should().BeApproximately(M0, 1e-3 * M0);

    [Fact]
    public void Pm_closed_form_matches_normalised_and_generalised_at_125()
    {
        var pm = Spectra.Normalised(new SpectrumSpec(SpectralShape.PiersonMoskowitz, Hs, Tp));
        foreach (double f in new[] { 0.4, 0.6, Fp, 1.0, 2.0 })
        {
            pm(f).Should().BeApproximately(Spectra.PiersonMoskowitz(f, Hs, Fp), 1e-4 * Spectra.PiersonMoskowitz(f, Hs, Fp));
            Spectra.GeneralizedPm(f, Hs, Fp, 1.25).Should().Be(Spectra.PiersonMoskowitz(f, Hs, Fp));
        }
    }

    [Fact]
    public void Jonswap_with_gamma_one_equals_pm()
    {
        var j = Spectra.Normalised(new SpectrumSpec(SpectralShape.Jonswap, Hs, Tp, Gamma: 1));
        foreach (double f in new[] { 0.3, 0.5, Fp, 0.9, 1.5, 3.0 })
            j(f).Should().BeApproximately(Spectra.PiersonMoskowitz(f, Hs, Fp), 1e-4 * Spectra.PiersonMoskowitz(f, Hs, Fp));
    }

    [Fact]
    public void Jonswap_peak_enhancement_and_asymmetric_sigma()
    {
        var j = Spectra.Normalised(new SpectrumSpec(SpectralShape.Jonswap, Hs, Tp, Gamma: 3.3));
        var pm = Spectra.Normalised(new SpectrumSpec(SpectralShape.PiersonMoskowitz, Hs, Tp));
        (j(Fp) / pm(Fp)).Should().BeGreaterThan(1.5, "γ concentrates energy at the peak");
        Spectra.JonswapShape(Fp, Fp, 3.3).Should().BeApproximately(3.3 * Spectra.JonswapShape(Fp, Fp, 1), 1e-12);
    }

    [Fact]
    public void Tma_equals_jonswap_where_omega_h_exceeds_two()
    {
        const double h = 0.3, g = 9.81;
        double f2 = 2 / (2 * Math.PI * Math.Sqrt(h / g)); // ω_h = 2
        Spectra.TmaFactor(f2 * 1.01, h, g).Should().Be(1);
        Spectra.TmaFactor(f2 * 0.25, h, g).Should().BeApproximately(0.5 * 0.5 * 0.5, 1e-12); // ω_h = 0.5
        Spectra.TmaFactor(f2 * 0.75, h, g).Should().BeApproximately(1 - 0.5 * 0.25, 1e-12);
        var spec = new SpectrumSpec(SpectralShape.Tma, Hs, Tp, DepthM: h);
        Spectra.Shape(spec, f2 * 1.5).Should().Be(Spectra.JonswapShape(f2 * 1.5, Fp, 3.3));
        Spectra.Shape(spec, f2 * 0.5).Should().BeLessThan(Spectra.JonswapShape(f2 * 0.5, Fp, 3.3));
    }

    [Fact]
    public void Darbyshire_is_zero_below_cutoff()
    {
        Spectra.DarbyshireShape(Fp - 0.05, Hs, Fp).Should().Be(0);
        Spectra.DarbyshireShape(Fp, Hs, Fp).Should().BeApproximately(1.429 * Hs * Hs, 1e-12);
    }
}
