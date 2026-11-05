using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

/// <summary>Spec §14 Phase 4: random synthesis, focusing, solitary, user-defined, Madsen second order.</summary>
public class WaveTypeTests
{
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 20 } };

    private static readonly WaveRequest RandomReq = new()
    {
        Type = WaveType.Random, WaterDepthM = 0.6, WaveHeightM = 0.05, PeriodS = 1.5, NumWaves = 200, RampS = 5,
    };

    private static string Hash(Trajectory t) =>
        Convert.ToHexString(SHA256.HashData(t.PositionsM.SelectMany(r => MemoryMarshal.AsBytes(r.AsSpan()).ToArray()).ToArray()));

    // ---- FFT convention ---------------------------------------------------------------------

    [Fact]
    public void Single_bin_synthesis_matches_the_documented_convention()
    {
        const int length = 1024;
        const double fs = 50, a = 0.02, eps = 0.7;
        int j = 30;
        double w = 2 * Math.PI * j * fs / length;
        var z = new Complex[length / 2 + 1];
        z[j] = Complex.FromPolarCoordinates(a, -eps);

        var eta = SpectralSynthesis.Surface(z, length, 200);
        for (int i = 0; i < 200; i++) eta[i].Should().BeApproximately(a * Math.Cos(w * i / fs - eps), 1e-12);

        var t = SpectralSynthesis.Transfer(z, length, fs, 0.6, Cfg);
        var x = SpectralSynthesis.Paddles(z, t, length, [0.25], 0, Enumerable.Repeat(1.0, 200).ToArray())[0];
        for (int i = 0; i < 200; i++) x[i].Should().BeApproximately((float)(a / t.C0[j] * Math.Sin(w * i / fs - eps)), 1e-7f);

        var back = SpectralSynthesis.SurfaceBins(SpectralSynthesis.Surface(z, length, length), length);
        (back[j] - z[j]).Magnitude.Should().BeLessThan(1e-12);
    }

    // ---- random -----------------------------------------------------------------------------

    [Fact]
    public void Same_seed_gives_identical_hash_different_seed_does_not()
    {
        var e = new WaveMathEngine(Cfg);
        var h1 = Hash(e.Calculate(RandomReq).Trajectory);
        Hash(e.Calculate(RandomReq).Trajectory).Should().Be(h1);
        Hash(e.Calculate(RandomReq with { RandomSeed = 7 }).Trajectory).Should().NotBe(h1);
    }

    [Fact]
    public void Path_a_and_path_b_produce_the_same_paddle_motion()
    {
        var e = new WaveMathEngine(Cfg);
        var a = e.Calculate(RandomReq).Trajectory;
        var b = e.Calculate(RandomReq with { Path = RandomPath.SurfaceThenTransfer }).Trajectory;
        float peak = a.PositionsM[0].Max(Math.Abs);
        for (int p = 0; p < a.PaddleCount; p++)
            for (int i = 0; i < a.SampleCount; i += 7)
                a.PositionsM[p][i].Should().BeApproximately(b.PositionsM[p][i], 1e-5f * peak);
    }

    [Fact]
    public void Band_limit_and_energy_report()
    {
        var r = new WaveMathEngine(Cfg).Calculate(RandomReq);
        var info = r.Random!;
        info.FLoHz.Should().BeApproximately(Math.Max(1 / 6.0, 0.4 / 1.5), 1e-12);
        info.FHiHz.Should().BeApproximately(Math.Min(1 / 0.6, 4 / 1.5), 1e-12);
        var s = Spectra.Normalised(new SpectrumSpec(SpectralShape.Jonswap, 0.05, 1.5));
        double expected = 100 * (1 - Spectra.Integrate(s, info.FLoHz, info.FHiHz, 20_000) / (0.05 * 0.05 / 16));
        info.EnergyRemovedPct.Should().BeApproximately(expected, 1e-9).And.BeInRange(0.1, 20);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        r.Trajectory.PositionsM.Should().OnlyContain(row => row[0] == 0);
        // Deterministic amplitudes: Hm0 of the realisation equals the band-limited target.
        info.RealisedHm0M.Should().BeApproximately(0.05 * Math.Sqrt(1 - expected / 100), 0.03 * 0.05);
        info.RepeatPeriodS.Should().BeGreaterThanOrEqualTo(info.RecordS);
        info.Components.Should().BeGreaterThan(100);
        r.Info.Should().HaveCount(2);
    }

    [Fact]
    public void Scale_to_target_hs_hits_the_target()
    {
        var e = new WaveMathEngine(Cfg);
        var raw = e.Calculate(RandomReq).Random!;
        raw.RealisedH13M.Should().BeApproximately(0.05, 0.15 * 0.05, "one seed scatters about ±10 %");
        var scaled = e.Calculate(RandomReq with { ScaleToTargetHs = true });
        scaled.Random!.RealisedH13M.Should().BeApproximately(0.05, 1e-9);
        WaveStatistics.ZeroUpcrossing(scaled.PredictedEtaM, 125, 5, 5).H13M.Should().BeApproximately(0.05, 1e-6);
        scaled.Random.ScaleFactor.Should().NotBe(1);
    }

    [Fact]
    public void Oblique_random_shifts_phase_between_paddles()
    {
        var r = new WaveMathEngine(Cfg).Calculate(RandomReq with { AngleDeg = 10 });
        r.Trajectory.PositionsM[0].Should().NotEqual(r.Trajectory.PositionsM[5]);
        r.Safety.Warnings.Select(w => w.Code).Should().Contain("OBLIQUE_WALLS");
    }

    [Fact]
    public void Darbyshire_runs_without_any_switch()
    {
        var r = new WaveMathEngine(Cfg).Calculate(RandomReq with { Spectrum = SpectralShape.Darbyshire });
        r.TargetSpectrum.Should().NotBeNull();
        r.Random!.Components.Should().BeGreaterThan(0);
    }

    // ---- focusing ---------------------------------------------------------------------------

    [Fact]
    public void Focusing_amplitudes_sum_to_crest_and_focus_time_is_validated()
    {
        var req = RandomReq with { Type = WaveType.Focusing, NumWaves = 40, FocusXM = 10, FocusTimeS = 30 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Info.Should().ContainSingle().Which.Should().Contain("x_f = 10");
        r.Trajectory.PositionsM[0][0].Should().Be(0);
        new WaveMathEngine(Cfg).Invoking(e => e.Calculate(req with { FocusTimeS = 2 })).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- solitary ---------------------------------------------------------------------------

    [Fact]
    public void Solitary_goring_stroke_and_velocity()
    {
        const double h = 0.6, hgt = 0.03;
        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest { Type = WaveType.Solitary, WaterDepthM = h, WaveHeightM = hgt });
        var x = r.Trajectory.PositionsM[0];
        x[0].Should().Be(0);
        x.Zip(x.Skip(1), (a, b) => b - a).Should().OnlyContain(d => d >= -1e-7f, "paddle only moves forward");
        // Small-amplitude limit of ∫ c η/h dt = 2H/(κh) = √(16Hh/3); the exact path is slightly shorter (h + η).
        double sLinear = Math.Sqrt(16 * hgt * h / 3);
        x[^1].Should().BeInRange((float)(0.9 * sLinear), (float)sLinear);
        double c = SolitaryWave.Celerity(hgt, h, 9.81);
        r.Safety.Worst.MaxAbsVelocityMs.Should().BeApproximately(c * hgt / (h + hgt), 0.02 * c * hgt / h);
        r.PredictedEtaM.Max().Should().BeApproximately(hgt, 1e-3 * hgt);
        r.Kinematics.Should().BeNull();
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
    }

    // ---- user defined -----------------------------------------------------------------------

    private static string Csv(Func<double, string> row, double seconds, double dt)
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf_user_{Guid.NewGuid():N}.csv");
        var lines = new List<string> { "time_s,value_m" };
        for (double t = 0; t <= seconds + 1e-9; t += dt) lines.Add(row(t));
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void User_wave_divides_by_transfer_function()
    {
        const double a = 0.02, T = 1.5;
        var path = Csv(t => FormattableString.Invariant($"{t},{a * Math.Cos(2 * Math.PI * t / T)}"), 60, 0.01);
        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest { Type = WaveType.UserDefinedWave, UserFilePath = path, RampS = 3 });
        var c = WaveComponent.Create(2 * a, T, 0.6, Cfg);
        var mid = r.Trajectory.PositionsM[0][(20 * 125)..(40 * 125)];
        ((double)mid.Max()).Should().BeApproximately(a / c.C0, 0.02 * a / c.C0);
        r.Components.Single().Wave.PeriodS.Should().BeApproximately(T, 0.02);
        r.Trajectory.PositionsM[0][0].Should().Be(0);
        File.Delete(path);
    }

    [Fact]
    public void User_stroke_is_used_directly()
    {
        var path = Csv(t => FormattableString.Invariant($"{t},{0.01 * Math.Sin(2 * Math.PI * t / 2)}"), 20, 0.004);
        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest { Type = WaveType.UserDefinedStroke, UserFilePath = path, SignalModPct = 50 });
        r.Trajectory.SampleRateHz.Should().Be(125);
        for (int i = 0; i < r.Trajectory.SampleCount; i += 37)
            r.Trajectory.PositionsM[3][i].Should().BeApproximately((float)(0.005 * Math.Sin(2 * Math.PI * (i / 125.0) / 2)), 2e-5f);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        File.Delete(path);

        var bad = Csv(t => FormattableString.Invariant($"{t},0,0"), 2, 0.1);
        new WaveMathEngine(Cfg).Invoking(e => e.Calculate(new WaveRequest { Type = WaveType.UserDefinedStroke, UserFilePath = bad }))
            .Should().Throw<InvalidDataException>().WithMessage("*1 or 6*");
        File.Delete(bad);
    }

    // ---- second order -----------------------------------------------------------------------

    [Fact]
    public void Madsen_amplitude_for_golden_case()
    {
        var c = WaveComponent.Create(0.05, 1.0, 0.6, Cfg);
        // Hand calculation: kh = 2.4508 → 3cosh/sinh³ = 0.0918, 2/c0 = 1.0889 → (0.0025/19.2)(−0.9971) = −1.298e-4 m.
        SecondOrder.MadsenPistonAmplitude(0.05, 0.6, c.Wave.Kh, c.C0).Should().BeApproximately(-1.298e-4, 0.01 * 1.298e-4);
    }

    [Fact]
    public void Stokes_bound_harmonic_deep_water_limit_is_k_a_squared_over_2()
    {
        const double hgt = 0.04, k = 20, a = hgt / 2;
        SecondOrder.StokesBoundAmplitude(hgt, k, 30).Should().BeApproximately(k * a * a / 2, 1e-9);
    }

    [Fact]
    public void Second_order_trajectory_adds_the_madsen_harmonic()
    {
        var req = new WaveRequest { Order = WaveOrder.Second, NumWaves = 20 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        var c = r.Components[0];
        double x2 = SecondOrder.MadsenPistonAmplitude(0.05, 0.6, c.Wave.Kh, c.C0);
        var x = r.Trajectory.PositionsM[0];
        double phase = OrientationPhase.PaddlePhase(c.Wave.K, 0.25, 0);
        for (int i = 5 * 125; i < 15 * 125; i += 11)
        {
            double t = i / 125.0;
            x[i].Should().BeApproximately((float)(c.StrokeAmplitudeM * Math.Sin(c.Wave.Omega * t + phase) + x2 * Math.Sin(2 * c.Wave.Omega * t)), 1e-6f);
        }
        r.Wavemaker.StrokeAmplitudeM.Should().BeApproximately(c.StrokeAmplitudeM + Math.Abs(x2), 1e-12);
        r.Info.Should().ContainSingle().Which.Should().Contain("Madsen");
        r.Safety.Ok.Should().BeTrue();
    }
}
