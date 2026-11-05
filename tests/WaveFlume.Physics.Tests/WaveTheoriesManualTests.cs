using System.Numerics;
using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

/// <summary>
/// Each formula of the "WAVE THEORIES" manual (IITM WaveGen theoretical manual, pages 26 to 29) written out again here
/// from the printed equation, and compared with the program.
/// </summary>
public class WaveTheoriesManualTests
{
    private const double G = 9.81;
    private const double Hs = 0.05, Fp = 1 / 1.5;
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 20 } };

    // ---- eq. (5): Pierson-Moskowitz; eq. (6): generalised PM -------------------------------

    [Fact]
    public void Eq5_pierson_moskowitz()
    {
        for (double f = 0.1; f < 3; f += 0.05)
        {
            double manual = 5.0 / 16 * Hs * Hs * Math.Pow(Fp, 4) / Math.Pow(f, 5) * Math.Exp(-5.0 / 4 * Math.Pow(f / Fp, -4));
            Spectra.PiersonMoskowitz(f, Hs, Fp).Should().BeApproximately(manual, 1e-12 * Math.Max(1, manual));
        }
    }

    [Theory]
    [InlineData(0.8)] [InlineData(1.25)] [InlineData(2.0)] [InlineData(3.0)]
    public void Eq6_generalised_pm_has_the_manuals_shape_and_beta_widens_or_narrows_it(double beta)
    {
        // The manual writes the factor 5/16, the program (b/4) so that the area is Hs^2/16 for every beta. The shape
        // f^-5 exp[-beta (f/fp)^-4] is the same, so the ratio is a constant, and after normalisation they coincide.
        double ratio = 0;
        for (double f = 0.2; f < 3; f += 0.1)
        {
            double manual = 5.0 / 16 * Hs * Hs * Math.Pow(Fp, 4) / Math.Pow(f, 5) * Math.Exp(-beta * Math.Pow(f / Fp, -4));
            double r = Spectra.GeneralizedPm(f, Hs, Fp, beta) / manual;
            if (ratio == 0) ratio = r;
            r.Should().BeApproximately(ratio, 1e-9 * ratio, "same shape at every frequency");
        }
        ratio.Should().BeApproximately(4 * beta / 5, 1e-9, "(beta/4) / (5/16)");

        var normalised = Spectra.Normalised(new SpectrumSpec(SpectralShape.GeneralizedPm, Hs, 1.5, Beta: beta));
        double Integral(Func<double, double> s) { double sum = 0, df = 1e-4; for (double f = df; f < 40 * Fp; f += df) sum += s(f) * df; return sum; }
        Integral(normalised).Should().BeApproximately(Hs * Hs / 16, 5e-4 * Hs * Hs / 16);
    }

    [Fact]
    public void Eq6_beta_1_25_is_the_standard_pm_larger_beta_is_narrower_smaller_is_wider()
    {
        double Width(double beta)
        {
            var s = Spectra.Normalised(new SpectrumSpec(SpectralShape.GeneralizedPm, Hs, 1.5, Beta: beta));
            double m0 = 0, m1 = 0, m2 = 0, df = 1e-4;
            for (double f = df; f < 40 * Fp; f += df) { double v = s(f) * df; m0 += v; m1 += f * v; m2 += f * f * v; }
            return Math.Sqrt(m0 * m2 / (m1 * m1) - 1); // spectral width parameter
        }
        for (double f = 0.2; f < 3; f += 0.2)
            Spectra.GeneralizedPm(f, Hs, Fp, 1.25).Should().BeApproximately(Spectra.PiersonMoskowitz(f, Hs, Fp), 1e-15);
        // Relative to the peak, the spread of energy about the mean frequency is larger for smaller beta.
        Width(0.8).Should().BeGreaterThan(Width(1.25));
        Width(2.0).Should().BeLessThan(Width(1.25));
    }

    // ---- eq. (7): JONSWAP -------------------------------------------------------------------

    [Theory]
    [InlineData(1.0)] [InlineData(3.3)] [InlineData(7.0)]
    public void Eq7_jonswap_shape_with_the_manuals_sigma_below_and_above_the_peak(double gamma)
    {
        for (double f = 0.2; f < 3; f += 0.05)
        {
            double sigma = f <= Fp ? 0.07 : 0.09;
            double manual = Math.Pow(f, -5) * Math.Exp(-5.0 / 4 * Math.Pow(f / Fp, -4))
                            * Math.Pow(gamma, Math.Exp(-0.5 * Math.Pow((f - Fp) / (sigma * Fp), 2)));
            Spectra.JonswapShape(f, Fp, gamma).Should().BeApproximately(manual, 1e-9 * Math.Max(1e-9, manual));
        }
    }

    [Fact]
    public void Eq7_gamma_1_reduces_to_pierson_moskowitz()
    {
        var jonswap = Spectra.Normalised(new SpectrumSpec(SpectralShape.Jonswap, Hs, 1.5, Gamma: 1));
        for (double f = 0.2; f < 3; f += 0.1)
            jonswap(f).Should().BeApproximately(Spectra.PiersonMoskowitz(f, Hs, Fp), 2e-3 * Spectra.PiersonMoskowitz(f, Hs, Fp) + 1e-12);
    }

    // ---- eq. (8): TMA -----------------------------------------------------------------------

    [Theory]
    [InlineData(0.5, 0.125)] [InlineData(1.0, 0.5)] [InlineData(1.5, 0.875)] [InlineData(2.0, 1.0)] [InlineData(3.0, 1.0)]
    public void Eq8_tma_depth_factor_phi(double omegaH, double expected)
    {
        // phi = 0.5 w^2 (w <= 1); 1 - 0.5 (2 - w)^2 (1 < w <= 2); 1 (w > 2), with w = 2 pi f sqrt(h / g)
        double depth = 0.6, f = omegaH / (2 * Math.PI * Math.Sqrt(depth / G));
        Spectra.TmaFactor(f, depth, G).Should().BeApproximately(expected, 1e-12);
    }

    [Fact]
    public void Eq8_tma_is_phi_times_jonswap_and_is_continuous()
    {
        const double depth = 0.6;
        for (double f = 0.1; f < 3; f += 0.05)
        {
            double wh = 2 * Math.PI * f * Math.Sqrt(depth / G);
            double phi = wh <= 1 ? 0.5 * wh * wh : wh <= 2 ? 1 - 0.5 * Math.Pow(2 - wh, 2) : 1;
            var spec = new SpectrumSpec(SpectralShape.Tma, Hs, 1.5, Gamma: 3.3, DepthM: depth);
            Spectra.Shape(spec, f).Should().BeApproximately(phi * Spectra.JonswapShape(f, Fp, 3.3), 1e-12 * Math.Max(1e-9, Spectra.JonswapShape(f, Fp, 3.3)));
        }
        double f1 = 1 / (2 * Math.PI * Math.Sqrt(depth / G)), f2 = 2 * f1; // w = 1 and w = 2
        Spectra.TmaFactor(f1 - 1e-9, depth, G).Should().BeApproximately(Spectra.TmaFactor(f1 + 1e-9, depth, G), 1e-6);
        Spectra.TmaFactor(f2 - 1e-9, depth, G).Should().BeApproximately(Spectra.TmaFactor(f2 + 1e-9, depth, G), 1e-6);
    }

    [Fact]
    public void Eq8_in_shallow_water_tma_has_less_high_frequency_energy_than_jonswap()
    {
        var tma = Spectra.Normalised(new SpectrumSpec(SpectralShape.Tma, Hs, 1.5, DepthM: 0.3));
        var jon = Spectra.Normalised(new SpectrumSpec(SpectralShape.Jonswap, Hs, 1.5));
        (tma(0.3) / tma(Fp)).Should().BeLessThan(jon(0.3) / jon(Fp), "phi < 1 reduces the low-frequency side in shallow water");
    }

    // ---- eq. (9): Darbyshire ----------------------------------------------------------------

    [Fact]
    public void Eq9_darbyshire_with_the_manuals_constant_0_00847()
    {
        for (double f = Fp - 0.04; f < 3; f += 0.03)
        {
            double d = f - Fp;
            double manual = 1.429 * Hs * Hs * Math.Exp(-Math.Sqrt(d * d / (0.00847 * (d + 0.042))));
            Spectra.DarbyshireShape(f, Hs, Fp).Should().BeApproximately(manual, 1e-12 * Math.Max(1, manual), $"f = {f:0.000}");
        }
        Spectra.DarbyshireShape(Fp - 0.042, Hs, Fp).Should().Be(0, "no energy at or below fp - 0.042 Hz");
        Spectra.DarbyshireShape(Fp - 0.1, Hs, Fp).Should().Be(0);
        Spectra.DarbyshireShape(Fp, Hs, Fp).Should().BeApproximately(1.429 * Hs * Hs, 1e-12, "peak at fp");
        // the earlier draft used 0.0047; the manual's 0.00847 gives a different (wider) shape
        double old = 1.429 * Hs * Hs * Math.Exp(-Math.Sqrt(0.2 * 0.2 / (0.0047 * (0.2 + 0.042))));
        Spectra.DarbyshireShape(Fp + 0.2, Hs, Fp).Should().NotBeApproximately(old, 1e-6);
    }

    [Fact]
    public void Eq9_darbyshire_is_normalised_and_makes_a_safe_random_sea()
    {
        var s = Spectra.Normalised(new SpectrumSpec(SpectralShape.Darbyshire, Hs, 1.5));
        double sum = 0, df = 1e-4;
        for (double f = df; f < 40 * Fp; f += df) sum += s(f) * df;
        sum.Should().BeApproximately(Hs * Hs / 16, 1e-3 * Hs * Hs / 16);

        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest
        {
            Type = WaveType.Random, Spectrum = SpectralShape.Darbyshire, WaveHeightM = Hs, PeriodS = 1.5, NumWaves = 200, RampS = 5, ScaleToTargetHs = true,
        });
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        r.Random!.RealisedH13M.Should().BeApproximately(Hs, 0.002);
    }

    // ---- elevated hinge transfer function ---------------------------------------------------

    [Theory]
    [InlineData(0.1, 0.0)] [InlineData(0.5, 0.0)] [InlineData(1.0, 0.1)] [InlineData(2.0, 0.2)] [InlineData(3.0, 0.3)] [InlineData(1.0, 0.45)]
    public void Elevated_hinge_transfer_function_matches_the_manual(double kh, double h0)
    {
        // H/S0 = 2 sinh(kh) [ (h-h0) k sinh(kh) - cosh(kh) + cosh(k h0) ] / ( k (h-h0) [ sinh(kh) cosh(kh) + kh ] )
        const double h = 0.6;
        double k = kh / h;
        double manual = 2 * Math.Sinh(kh) * ((h - h0) * k * Math.Sinh(kh) - Math.Cosh(kh) + Math.Cosh(k * h0))
                        / (k * (h - h0) * (Math.Sinh(kh) * Math.Cosh(kh) + kh));
        TransferFunctions.ElevatedHinge(k, h, h0).Should().BeApproximately(manual, 1e-9 * Math.Abs(manual));
    }

    [Fact]
    public void Elevated_hinge_at_the_bed_is_the_flap_and_a_higher_hinge_moves_less_water_per_unit_stroke()
    {
        // S0 is the stroke at the water surface. The higher the hinge, the smaller the part of the depth that moves, so the
        // wave made per unit surface stroke falls: piston > flap (hinge on the bed) > hinge at 0.2 m > hinge at 0.4 m.
        const double h = 0.6;
        foreach (double kh in new[] { 0.3, 1.0, 2.0 })
        {
            double k = kh / h;
            TransferFunctions.ElevatedHinge(k, h, 0).Should().BeApproximately(TransferFunctions.Flap(k, h), 1e-12);
            double flap = TransferFunctions.Flap(k, h);
            double low = TransferFunctions.ElevatedHinge(k, h, 0.2), high = TransferFunctions.ElevatedHinge(k, h, 0.4);
            TransferFunctions.Piston(kh).Should().BeGreaterThan(flap);
            flap.Should().BeGreaterThan(low);
            low.Should().BeGreaterThan(high);
            high.Should().BeGreaterThan(0);
        }
    }
    // ---- bichromatic waves, Section 1.1.2.2 -------------------------------------------------

    [Fact]
    public void Bichromatic_target_is_the_sum_of_two_cosines_and_each_gets_its_own_stroke()
    {
        const double h = 0.6;
        var req = new WaveRequest
        {
            Type = WaveType.Bichromatic, WaterDepthM = h, WaveHeightM = 0.04, PeriodS = 1.0, Height2M = 0.03, Period2S = 1.25,
            NumWaves = 60, RampS = 5,
        };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        r.Components.Should().HaveCount(2);

        double[] heights = [0.04, 0.03], periods = [1.0, 1.25];
        var x = r.Trajectory.PositionsM[0];
        for (int i = 0; i < 2; i++)
        {
            double w = 2 * Math.PI / periods[i];
            double k = Dispersion.Solve(periods[i], h, G).K;
            double expected = heights[i] / (2 * TransferFunctions.Piston(k * h)); // a_i / c0_i with a_i = H_i / 2
            // amplitude of this frequency in the paddle motion, over whole periods of both waves (10 s to 50 s)
            Complex sum = 0;
            int i0 = 10 * 125, i1 = 50 * 125;
            for (int s = i0; s < i1; s++) sum += x[s] * Complex.Exp(-Complex.ImaginaryOne * w * s / 125.0);
            (2 * sum / (i1 - i0)).Magnitude.Should().BeApproximately(expected, 5e-5, $"component {i + 1}");
        }

        // and the predicted surface at the paddle is the sum of the two cosines
        double t0 = 20.0;
        int idx = (int)(t0 * 125);
        double target = 0.02 * Math.Cos(2 * Math.PI / 1.0 * t0) + 0.015 * Math.Cos(2 * Math.PI / 1.25 * t0);
        r.PredictedEtaM[idx].Should().BeApproximately(target, 1e-9);
    }

    [Fact]
    public void Bichromatic_beat_period_follows_from_the_two_frequencies()
    {
        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest
        {
            Type = WaveType.Bichromatic, WaveHeightM = 0.03, PeriodS = 1.0, Height2M = 0.03, Period2S = 1.25, NumWaves = 40, RampS = 5,
        });
        // The beat period is 1 / (1/T1 - 1/T2) = 5 s. Both cosines equal 1 together at t = 20 s (heights add) and have
        // opposite signs at t = 22.5 s (heights cancel).
        double beat = 1 / (1 / 1.0 - 1 / 1.25);
        beat.Should().BeApproximately(5, 1e-12);
        r.PredictedEtaM[20 * 125].Should().BeApproximately(0.03, 1e-9, "crests in step: a1 + a2");
        r.PredictedEtaM[(int)(22.5 * 125)].Should().BeApproximately(0, 1e-4, "crest meets trough: a1 - a2 (22.5 s lies between two samples)");
    }
    // ---- every wave type calculates and is safe ---------------------------------------------

    public static IEnumerable<object[]> EveryType()
    {
        yield return [new WaveRequest { Type = WaveType.Monochromatic, NumWaves = 20 }];
        yield return [new WaveRequest { Type = WaveType.Monochromatic, Order = WaveOrder.Second, NumWaves = 20 }];
        yield return [new WaveRequest { Type = WaveType.Bichromatic, Height2M = 0.03, Period2S = 1.3, NumWaves = 40, RampS = 5 }];
        foreach (var shape in Enum.GetValues<SpectralShape>())
            yield return [new WaveRequest { Type = WaveType.Random, Spectrum = shape, WaveHeightM = 0.04, PeriodS = 1.5, NumWaves = 100, RampS = 5 }];
        yield return [new WaveRequest { Type = WaveType.Cnoidal, WaterDepthM = 0.4, WaveHeightM = 0.04, PeriodS = 4, NumWaves = 12, RampS = 4 }];
        yield return [new WaveRequest { Type = WaveType.Solitary, WaterDepthM = 0.4, WaveHeightM = 0.03 }];
        yield return [new WaveRequest { Type = WaveType.Focusing, WaveHeightM = 0.06, PeriodS = 1.5, NumWaves = 40, RampS = 5, FocusXM = 10, FocusTimeS = 30 }];
    }

    [Theory]
    [MemberData(nameof(EveryType))]
    public void Every_wave_type_calculates_safely_and_every_motion_starts_from_zero(WaveRequest req)
    {
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue($"{req.Type} {req.Spectrum}: " + string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        r.Trajectory.PaddleCount.Should().Be(6);
        r.Trajectory.SampleCount.Should().BeGreaterThan(100);
        r.Trajectory.PositionsM.Should().OnlyContain(row => row[0] == 0);
        r.Trajectory.PositionsM.SelectMany(row => row).Should().OnlyContain(v => !float.IsNaN(v) && !float.IsInfinity(v));
    }
}
