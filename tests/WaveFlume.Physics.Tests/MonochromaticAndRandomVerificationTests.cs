using System.Numerics;
using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

/// <summary>
/// White-box verification of the maths behind the two wave types the product is used for: Monochromatic and
/// Random (Generalised Pierson-Moskowitz, JONSWAP). Every expected value is recomputed here from the textbook
/// formula, not by calling the production code, so a wrong formula cannot agree with itself.
/// </summary>
public class MonochromaticAndRandomVerificationTests
{
    private const double G = 9.81;
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 20 } };
    private static WaveMathEngine Engine => new(Cfg);

    // ---- independent reference implementations ----------------------------------------------

    /// <summary>Wave number by bisection on omega^2 = g k tanh(k h); no Newton, no shared code.</summary>
    private static double WaveNumber(double periodS, double depthM)
    {
        double w = 2 * Math.PI / periodS, lo = 1e-9, hi = 1000;
        for (int i = 0; i < 200; i++)
        {
            double mid = (lo + hi) / 2;
            if (G * mid * Math.Tanh(mid * depthM) - w * w > 0) hi = mid; else lo = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>Biesel piston transfer function H/S0 = 2 (cosh 2kh - 1) / (sinh 2kh + 2kh).</summary>
    private static double Biesel(double kh) => 2 * (Math.Cosh(2 * kh) - 1) / (Math.Sinh(2 * kh) + 2 * kh);

    /// <summary>Complex amplitude of x at angular frequency w over whole periods in [t0, t1).</summary>
    private static Complex Phasor(float[] x, double fs, double w, double t0, double t1)
    {
        int i0 = (int)Math.Round(t0 * fs), i1 = (int)Math.Round(t1 * fs);
        Complex sum = 0;
        for (int i = i0; i < i1; i++) sum += x[i] * Complex.Exp(-Complex.ImaginaryOne * w * i / fs);
        return 2 * sum / (i1 - i0);
    }

    /// <summary>Spectral density integral by a fine trapezoid rule, independent of Spectra.Integrate.</summary>
    private static double Integral(Func<double, double> s, double fMax, int n = 400_000)
    {
        double df = fMax / n, sum = 0.5 * s(fMax);
        for (int i = 1; i < n; i++) sum += s(i * df);
        return sum * df;
    }

    // ---- dispersion and transfer function ---------------------------------------------------

    [Theory]
    [InlineData(0.5, 0.2)] [InlineData(0.5, 1.0)] [InlineData(0.6, 0.8)] [InlineData(1.0, 0.6)]
    [InlineData(1.5, 0.4)] [InlineData(2.0, 0.8)] [InlineData(3.0, 0.2)] [InlineData(4.0, 0.6)] [InlineData(6.0, 0.2)] [InlineData(6.0, 1.0)]
    public void Dispersion_solution_matches_an_independent_bisection_and_satisfies_the_relation(double t, double h)
    {
        var w = Dispersion.Solve(t, h, G);
        w.K.Should().BeApproximately(WaveNumber(t, h), 1e-9 * w.K);
        double omega = 2 * Math.PI / t;
        (G * w.K * Math.Tanh(w.K * h)).Should().BeApproximately(omega * omega, 1e-9 * omega * omega);
        w.WaveLengthM.Should().BeApproximately(2 * Math.PI / WaveNumber(t, h), 1e-6);
    }

    [Fact]
    public void Dispersion_reduces_to_the_deep_and_shallow_water_limits()
    {
        var deep = Dispersion.Solve(0.5, 5.0, G);
        deep.WaveLengthM.Should().BeApproximately(G * 0.5 * 0.5 / (2 * Math.PI), 1e-4, "L = g T^2 / 2 pi in deep water");
        deep.GroupVelocityMs.Should().BeApproximately(deep.CelerityMs / 2, 1e-4, "Cg = C/2 in deep water");

        var shallow = Dispersion.Solve(60, 0.2, G);
        shallow.CelerityMs.Should().BeApproximately(Math.Sqrt(G * 0.2), 0.005 * Math.Sqrt(G * 0.2), "C = sqrt(g h) in shallow water");
        shallow.GroupVelocityMs.Should().BeApproximately(shallow.CelerityMs, 0.01 * shallow.CelerityMs, "Cg = C in shallow water");
    }

    [Fact]
    public void Piston_transfer_function_equals_the_biesel_closed_form()
    {
        foreach (double kh in new[] { 0.01, 0.05, 0.1, 0.3, 0.5, 1, 2, 3, 5, 8, 12, 20 })
            TransferFunctions.Piston(kh).Should().BeApproximately(Biesel(kh), 1e-9 * Biesel(kh), $"kh = {kh}");
    }

    [Fact]
    public void Piston_transfer_function_has_the_known_limits_and_never_decreases()
    {
        TransferFunctions.Piston(0.02).Should().BeApproximately(0.02, 0.0005, "shallow water: H/S0 -> kh");
        TransferFunctions.Piston(15).Should().BeApproximately(2.0, 1e-6, "deep water: H/S0 -> 2");
        double previous = 0;
        for (double kh = 0.01; kh <= 20; kh += 0.01)
        {
            double c = TransferFunctions.Piston(kh);
            c.Should().BeGreaterThanOrEqualTo(previous);
            c.Should().BeLessThan(2.0 + 1e-9);
            previous = c;
        }
    }

    [Fact]
    public void Flap_transfer_function_matches_the_standard_formula_and_is_below_the_piston()
    {
        foreach (double kh in new[] { 0.1, 0.5, 1, 2, 3, 6 })
        {
            double k = kh / 0.6;
            double standard = 4 * (Math.Sinh(kh) / kh) * (kh * Math.Sinh(kh) - Math.Cosh(kh) + 1) / (Math.Sinh(2 * kh) + 2 * kh);
            TransferFunctions.Flap(k, 0.6).Should().BeApproximately(standard, 1e-9 * standard, $"kh = {kh}");
            TransferFunctions.Flap(k, 0.6).Should().BeLessThan(TransferFunctions.Piston(kh));
        }
    }

    // ---- monochromatic (regular) waves ------------------------------------------------------

    [Theory]
    [InlineData(0.6, 0.05, 1.0)] [InlineData(0.4, 0.03, 2.0)] [InlineData(0.8, 0.08, 1.5)]
    [InlineData(0.6, 0.02, 0.7)] [InlineData(0.3, 0.02, 3.0)] [InlineData(0.8, 0.10, 4.0)]
    public void Monochromatic_stroke_velocity_and_acceleration_follow_from_the_wave_height(double h, double height, double t)
    {
        var r = Engine.Calculate(new WaveRequest { WaterDepthM = h, WaveHeightM = height, PeriodS = t, NumWaves = 20, RampS = 2 });
        double k = WaveNumber(t, h), w = 2 * Math.PI / t;
        double xa = height / (2 * Biesel(k * h)); // H = (H/S0) * S0 and S0 = 2 Xa

        r.Wavemaker.C0.Should().BeApproximately(Biesel(k * h), 1e-9);
        r.Wavemaker.StrokeAmplitudeM.Should().BeApproximately(xa, 1e-9 * xa);
        r.Wavemaker.StrokePeakToPeakM.Should().BeApproximately(2 * xa, 1e-9 * xa);
        r.Wavemaker.PeakVelocityMs.Should().BeApproximately(xa * w, 1e-9 * xa * w);
        r.Wavemaker.PeakAccelMs2.Should().BeApproximately(xa * w * w, 1e-9 * xa * w * w);
        r.Kinematics!.K.Should().BeApproximately(k, 1e-9 * k);
        r.Kinematics.WaveLengthM.Should().BeApproximately(2 * Math.PI / k, 1e-6);
        r.Kinematics.CelerityMs.Should().BeApproximately(w / k, 1e-6);
        r.Kinematics.UMaxMs.Should().BeApproximately(Math.PI * height / t, 1e-9, "u = pi H / T");
    }

    [Fact]
    public void Golden_case_matches_the_published_numbers()
    {
        // Depth 0.6 m, H = 0.05 m, T = 1 s -> stroke amplitude 13.6 mm (hand calculation in the specification).
        var r = Engine.Calculate(new WaveRequest { NumWaves = 20, RampS = 2 });
        r.Wavemaker.StrokeAmplitudeM.Should().BeApproximately(0.0136, 0.0001);
        r.Safety.Ok.Should().BeTrue();
    }

    [Fact]
    public void Monochromatic_trajectory_is_a_sine_of_the_right_size_and_period_on_every_paddle()
    {
        const double h = 0.6, height = 0.05, t = 1.0;
        var r = Engine.Calculate(new WaveRequest { WaterDepthM = h, WaveHeightM = height, PeriodS = t, NumWaves = 20, RampS = 2 });
        var traj = r.Trajectory;
        double xa = r.Wavemaker.StrokeAmplitudeM, w = 2 * Math.PI / t;

        traj.PaddleCount.Should().Be(6);
        traj.SampleCount.Should().Be(20 * 125 + 1);
        traj.DurationS.Should().BeApproximately(20, 1e-9);

        for (int p = 1; p < traj.PaddleCount; p++)
            traj.PositionsM[p].Should().Equal(traj.PositionsM[0], "at a wave angle of 0 all paddles move together");

        var a = Phasor(traj.PositionsM[0], 125, w, 3, 13);
        a.Magnitude.Should().BeApproximately(xa, 1e-5, "steady-state amplitude");
        double period = 2 * Math.PI / w;
        var stats = WaveStatistics.ZeroUpcrossing(traj.PositionsM[0].Select(x => (double)x).ToArray(), 125, 3, 3);
        stats.TzS.Should().BeApproximately(period, 0.002);
        stats.HmaxM.Should().BeApproximately(2 * xa, 0.003 * 2 * xa, "peak to peak = 2 Xa");

        var worst = SafetyGatekeeper.ScanTrajectory(traj);
        worst.MaxAbsPositionM.Should().BeApproximately(xa, 0.001 * xa);
        worst.MaxAbsVelocityMs.Should().BeApproximately(xa * w, 0.002 * xa * w);
        worst.MaxAbsAccelMs2.Should().BeApproximately(xa * w * w, 0.002 * xa * w * w);
    }

    [Fact]
    public void Ramp_starts_and_ends_at_zero_and_follows_the_half_cosine_envelope()
    {
        var r = Engine.Calculate(new WaveRequest { NumWaves = 20, RampS = 4 });
        var x = r.Trajectory.PositionsM[0];
        double xa = r.Wavemaker.StrokeAmplitudeM;
        x[0].Should().Be(0);
        Math.Abs(x[^1]).Should().BeLessThan(1e-6f);
        for (int i = 0; i < x.Length; i++)
        {
            double t = i / 125.0, up = t >= 4 ? 1 : 0.5 * (1 - Math.Cos(Math.PI * t / 4));
            double down = 20 - t >= 4 ? 1 : 0.5 * (1 - Math.Cos(Math.PI * (20 - t) / 4));
            Math.Abs(x[i]).Should().BeLessThanOrEqualTo((float)(xa * up * down + 1e-6), $"sample {i}");
        }
    }

    [Fact]
    public void Predicted_elevation_has_half_the_wave_height_as_amplitude()
    {
        var r = Engine.Calculate(new WaveRequest { WaveHeightM = 0.05, NumWaves = 20, RampS = 2 });
        r.PredictedEtaM.Max().Should().BeApproximately(0.025, 0.0001);
        r.PredictedEtaM.Min().Should().BeApproximately(-0.025, 0.0001);
    }

    [Fact]
    public void Signal_modification_scales_the_whole_motion()
    {
        var full = Engine.Calculate(new WaveRequest { NumWaves = 20, RampS = 2 });
        var half = Engine.Calculate(new WaveRequest { NumWaves = 20, RampS = 2, SignalModPct = 50 });
        half.Wavemaker.StrokeAmplitudeM.Should().BeApproximately(0.5 * full.Wavemaker.StrokeAmplitudeM, 1e-12);
        half.Wavemaker.PeakVelocityMs.Should().BeApproximately(0.5 * full.Wavemaker.PeakVelocityMs, 1e-12);
        for (int i = 0; i < full.Trajectory.SampleCount; i += 11)
            half.Trajectory.PositionsM[2][i].Should().BeApproximately(0.5f * full.Trajectory.PositionsM[2][i], 1e-7f);
    }

    [Theory]
    [InlineData(5)] [InlineData(10)] [InlineData(20)]
    public void Oblique_wave_gives_each_neighbouring_paddle_a_phase_step_of_k_b_sin_theta(double angleDeg)
    {
        const double h = 0.6, t = 1.0;
        var r = Engine.Calculate(new WaveRequest { WaterDepthM = h, WaveHeightM = 0.04, PeriodS = t, NumWaves = 20, RampS = 2, AngleDeg = angleDeg });
        double k = WaveNumber(t, h), w = 2 * Math.PI / t;
        double expectedStep = k * Cfg.PaddleWidthM * Math.Sin(angleDeg * Math.PI / 180);
        var phasors = r.Trajectory.PositionsM.Select(row => Phasor(row, 125, w, 3, 13)).ToArray();
        for (int p = 0; p + 1 < phasors.Length; p++)
        {
            Math.Abs(phasors[p + 1].Magnitude - phasors[p].Magnitude).Should().BeLessThan(1e-5, "every paddle has the same amplitude");
            double step = Math.Abs(Math.IEEERemainder((phasors[p + 1] / phasors[p]).Phase, 2 * Math.PI));
            step.Should().BeApproximately(expectedStep, 1e-3, $"paddles {p + 1} and {p + 2}");
        }
    }

    [Fact]
    public void Second_order_correction_is_added_for_a_regular_wave_and_is_small_compared_with_the_first_order_stroke()
    {
        var first = Engine.Calculate(new WaveRequest { WaveHeightM = 0.05, NumWaves = 20, RampS = 2 });
        var second = Engine.Calculate(new WaveRequest { Order = WaveOrder.Second, WaveHeightM = 0.05, NumWaves = 20, RampS = 2 });
        second.Safety.Ok.Should().BeTrue();
        second.Wavemaker.StrokeAmplitudeM.Should().BeGreaterThan(first.Wavemaker.StrokeAmplitudeM);
        second.Wavemaker.StrokeAmplitudeM.Should().BeLessThan(1.2 * first.Wavemaker.StrokeAmplitudeM);
    }

    // ---- spectra ----------------------------------------------------------------------------

    [Theory]
    [InlineData(0.6)] [InlineData(0.8)] [InlineData(1.25)] [InlineData(2.0)] [InlineData(3.0)]
    public void Generalised_pm_integrates_to_hs_squared_over_16_for_any_beta(double beta)
    {
        const double hs = 0.05, fp = 1 / 1.5;
        Integral(f => Spectra.GeneralizedPm(f, hs, fp, beta), 40 * fp).Should().BeApproximately(hs * hs / 16, 2e-4 * hs * hs / 16);
    }

    [Fact]
    public void Generalised_pm_with_beta_1_25_is_exactly_pierson_moskowitz()
    {
        const double hs = 0.05, fp = 1 / 1.5;
        for (double f = 0.1; f < 3; f += 0.07)
        {
            Spectra.GeneralizedPm(f, hs, fp, 1.25).Should().BeApproximately(Spectra.PiersonMoskowitz(f, hs, fp), 1e-15);
            // S = (5/16) Hs^2 fp^4 f^-5 exp(-1.25 (fp/f)^4), written out
            double pm = 5.0 / 16 * hs * hs * Math.Pow(fp, 4) * Math.Pow(f, -5) * Math.Exp(-1.25 * Math.Pow(fp / f, 4));
            Spectra.PiersonMoskowitz(f, hs, fp).Should().BeApproximately(pm, 1e-12 * Math.Max(1, pm));
        }
    }

    [Theory]
    [InlineData(0.8)] [InlineData(1.25)] [InlineData(2.0)]
    public void Generalised_pm_peaks_where_the_derivative_vanishes(double beta)
    {
        const double fp = 1 / 1.5;
        double expected = fp * Math.Pow(4 * beta / 5, 0.25); // d/df [f^-5 exp(-beta (fp/f)^4)] = 0
        double best = 0, bestF = 0;
        for (double f = 0.2; f < 2; f += 1e-5)
        {
            double s = Spectra.GeneralizedPm(f, 0.05, fp, beta);
            if (s > best) { best = s; bestF = f; }
        }
        bestF.Should().BeApproximately(expected, 1e-4);
    }

    [Fact]
    public void Jonswap_peaks_at_the_peak_frequency_and_reduces_to_pm_for_gamma_1()
    {
        const double hs = 0.05, fp = 1 / 1.5;
        var spec = new SpectrumSpec(SpectralShape.Jonswap, hs, 1.5, Gamma: 3.3);
        var s = Spectra.Normalised(spec);
        double best = 0, bestF = 0;
        for (double f = 0.2; f < 2; f += 1e-5)
            if (s(f) > best) { best = s(f); bestF = f; }
        bestF.Should().BeApproximately(fp, 1e-4);

        var gamma1 = Spectra.Normalised(spec with { Gamma = 1 });
        for (double f = 0.15; f < 2.5; f += 0.05)
            gamma1(f).Should().BeApproximately(Spectra.PiersonMoskowitz(f, hs, fp), 2e-3 * Spectra.PiersonMoskowitz(f, hs, fp) + 1e-12);
    }

    [Theory]
    [InlineData(1.0)] [InlineData(3.3)] [InlineData(7.0)]
    public void Jonswap_is_normalised_to_hs_squared_over_16_and_a_larger_gamma_gives_a_narrower_peak(double gamma)
    {
        const double hs = 0.05, tp = 1.5, fp = 1 / tp;
        var s = Spectra.Normalised(new SpectrumSpec(SpectralShape.Jonswap, hs, tp, Gamma: gamma));
        Integral(s, 40 * fp).Should().BeApproximately(hs * hs / 16, 2e-4 * hs * hs / 16);
        // Same energy in a narrower band means a higher peak.
        var pm = Spectra.Normalised(new SpectrumSpec(SpectralShape.Jonswap, hs, tp, Gamma: 1));
        if (gamma > 1) s(fp).Should().BeGreaterThan(pm(fp));
    }

    // ---- random waves -----------------------------------------------------------------------

    public static IEnumerable<object[]> RandomShapes() =>
    [
        [SpectralShape.Jonswap, 3.3, 1.25], [SpectralShape.Jonswap, 1.0, 1.25], [SpectralShape.Jonswap, 7.0, 1.25],
        [SpectralShape.GeneralizedPm, 3.3, 1.25], [SpectralShape.GeneralizedPm, 3.3, 2.0], [SpectralShape.GeneralizedPm, 3.3, 0.9],
    ];

    [Theory]
    [MemberData(nameof(RandomShapes))]
    public void Random_sea_realises_the_requested_significant_wave_height_and_peak_period(SpectralShape shape, double gamma, double beta)
    {
        var req = new WaveRequest
        {
            Type = WaveType.Random, Spectrum = shape, Gamma = gamma, Beta = beta, WaveHeightM = 0.05, PeriodS = 1.5,
            NumWaves = 400, RampS = 5, ScaleToTargetHs = true, RandomSeed = 4321,
        };
        var r = Engine.Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));

        var info = r.Random!;
        info.EnergyRemovedPct.Should().BeLessThan(8, "the usable band holds nearly all the energy");
        info.RealisedH13M.Should().BeApproximately(0.05, 0.002, "scaled so the zero-crossing H1/3 equals Hs");

        int skip = (int)(5 * 125);
        var eta = r.PredictedEtaM.Skip(skip).Take(r.PredictedEtaM.Length - 2 * skip).ToArray();
        var spec = WaveStatistics.Spectral(eta, 125, 16384);
        spec.Hm0M.Should().BeApproximately(0.05, 0.06 * 0.05, "spectral Hm0 agrees with Hs");

        // The realised peak lies near the spectrum's own peak (which for a Generalised PM depends on beta).
        double fpk = shape == SpectralShape.GeneralizedPm ? Math.Pow(4 * beta / 5, 0.25) / 1.5 : 1 / 1.5;
        (1 / spec.TpS).Should().BeApproximately(fpk, 0.15 * fpk);
    }

    [Theory]
    [MemberData(nameof(RandomShapes))]
    public void Random_paddle_motion_starts_and_ends_at_rest_and_is_repeatable(SpectralShape shape, double gamma, double beta)
    {
        var req = new WaveRequest
        {
            Type = WaveType.Random, Spectrum = shape, Gamma = gamma, Beta = beta, WaveHeightM = 0.04, PeriodS = 1.2,
            NumWaves = 100, RampS = 4, RandomSeed = 99,
        };
        var a = Engine.Calculate(req).Trajectory;
        var b = Engine.Calculate(req).Trajectory;
        var other = Engine.Calculate(req with { RandomSeed = 100 }).Trajectory;

        for (int p = 0; p < a.PaddleCount; p++)
        {
            a.PositionsM[p][0].Should().Be(0);
            Math.Abs(a.PositionsM[p][^1]).Should().BeLessThan(1e-4f);
            a.PositionsM[p].Should().Equal(b.PositionsM[p], "the same seed gives exactly the same sea");
        }
        float maxDiff = a.PositionsM[0].Zip(other.PositionsM[0], (x, y) => Math.Abs(x - y)).Max();
        maxDiff.Should().BeGreaterThan(1e-4f, "a different seed gives a different sea");
    }

    [Fact]
    public void Random_paddle_stroke_scales_linearly_with_the_requested_hs()
    {
        var small = Engine.Calculate(new WaveRequest { Type = WaveType.Random, WaveHeightM = 0.03, PeriodS = 1.5, NumWaves = 100, RampS = 4 });
        var large = Engine.Calculate(new WaveRequest { Type = WaveType.Random, WaveHeightM = 0.06, PeriodS = 1.5, NumWaves = 100, RampS = 4 });
        (large.Wavemaker.StrokeAmplitudeM / small.Wavemaker.StrokeAmplitudeM).Should().BeApproximately(2.0, 1e-6);
    }
}
