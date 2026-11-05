using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

/// <summary>
/// Black-box tests: only the inputs and the verdict are looked at. Each input is tried just inside and just
/// outside its allowed range (boundary value analysis); the verdict must flip exactly at the boundary.
/// Default limits: depth 0.2-0.8 m, height 0.01-0.30 m, period 0.6-6 s, 10-10000 waves, 50-200 Hz, stroke 0.35 m.
/// </summary>
public class BoundaryValueTests
{
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 20 } };

    private static HashSet<string> Violations(WaveRequest r, FlumeConfig? cfg = null) =>
        new WaveMathEngine(cfg ?? Cfg).Calculate(r).Safety.Violations.Select(v => v.Code).ToHashSet();

    private static WaveRequest Reg(double depth = 0.6, double height = 0.02, double period = 1.0) =>
        new() { WaterDepthM = depth, WaveHeightM = height, PeriodS = period, NumWaves = 20, RampS = 2 };

    // ---- each input at and around its limits ------------------------------------------------

    [Theory]
    [InlineData(0.19, true)] [InlineData(0.2, false)] [InlineData(0.5, false)] [InlineData(0.8, false)] [InlineData(0.81, true)] [InlineData(1.5, true)]
    public void Water_depth_range(double depth, bool blocked) =>
        Violations(Reg(depth: depth)).Contains("DEPTH_RANGE").Should().Be(blocked);

    [Theory]
    [InlineData(0.005, true)] [InlineData(0.0099, true)] [InlineData(0.01, false)] [InlineData(0.15, false)] [InlineData(0.30, false)] [InlineData(0.3001, true)] [InlineData(0.5, true)]
    public void Wave_height_range(double height, bool blocked) =>
        Violations(Reg(depth: 0.8, height: height, period: 2.0)).Contains("HEIGHT_RANGE").Should().Be(blocked);

    [Theory]
    [InlineData(0.25, "PERIOD_FLOOR")] [InlineData(0.59, "PERIOD_RANGE")] [InlineData(0.6, null)] [InlineData(1.0, null)] [InlineData(6.0, null)] [InlineData(6.01, "PERIOD_RANGE")] [InlineData(8.0, "PERIOD_RANGE")]
    public void Wave_period_range(double period, string? code)
    {
        var v = Violations(Reg(height: 0.01, period: period));
        if (code is null) v.Should().NotContain(["PERIOD_RANGE", "PERIOD_FLOOR"]);
        else v.Should().Contain(code);
    }

    [Theory]
    [InlineData(9, true)] [InlineData(10, false)] [InlineData(11, false)] [InlineData(10_000, false)] [InlineData(10_001, true)] [InlineData(0, true)] [InlineData(-5, true)]
    public void Number_of_waves_range(int waves, bool blocked)
    {
        var req = Reg(height: 0.01, period: 0.6) with { NumWaves = waves };
        // Outside the range the trajectory cannot even be built for non-positive counts: that is also a refusal.
        bool refused;
        try { refused = Violations(req).Contains("NUM_WAVES_RANGE"); }
        catch (ArgumentOutOfRangeException) { refused = true; }
        refused.Should().Be(blocked);
    }

    [Theory]
    [InlineData(49, true)] [InlineData(50, false)] [InlineData(125, false)] [InlineData(200, false)] [InlineData(201, true)]
    public void Sampling_frequency_range(double fs, bool blocked) =>
        Violations(Reg(height: 0.01) with { SamplingHz = fs }).Contains("FS_RANGE").Should().Be(blocked);

    // ---- limits of the paddle -----------------------------------------------------------------

    [Fact]
    public void Wave_height_just_below_the_breaking_limit_passes_and_just_above_is_blocked()
    {
        const double depth = 0.2, period = 1.0;
        var wave = Dispersion.Solve(period, depth, 9.81);
        double breaking = 0.142 * wave.WaveLengthM * Math.Tanh(wave.Kh); // Miche
        Violations(Reg(depth, breaking * 0.99, period)).Should().NotContain("BREAKING");
        Violations(Reg(depth, breaking * 1.01, period)).Should().Contain("BREAKING");
    }

    [Fact]
    public void Stroke_limit_flips_between_a_wave_needing_95_percent_and_105_percent_of_the_stroke()
    {
        const double depth = 0.6, period = 6.0;
        var wave = Dispersion.Solve(period, depth, 9.81);
        double c0 = TransferFunctions.Piston(wave.Kh);
        double heightAtLimit = 2 * c0 * 0.35; // Xa = H / (2 c0) = 0.35 m
        Violations(Reg(depth, heightAtLimit * 0.95, period) with { NumWaves = 20 }).Should().NotContain("STROKE");
        Violations(Reg(depth, heightAtLimit * 1.05, period)).Should().Contain("STROKE");
    }

    [Fact]
    public void Acceleration_limit_flips_just_above_and_just_below_the_wave_peak_acceleration()
    {
        var req = Reg(0.6, 0.10, 1.0);
        double peak = new WaveMathEngine(Cfg).Calculate(req).Wavemaker.PeakAccelMs2;
        FlumeConfig WithLimit(double a) => Cfg with { Limits = Cfg.Limits with { MaxAccelMs2 = a } };
        Violations(req, WithLimit(peak * 1.02)).Should().NotContain("ACCELERATION");
        Violations(req, WithLimit(peak * 0.98)).Should().Contain("ACCELERATION");
    }

    [Fact]
    public void Velocity_limit_blocks_a_wave_that_needs_too_much_paddle_speed()
    {
        // H = 0.30 m, T = 2 s, h = 0.8 m needs Xa = 0.147 m and 0.46 m/s; four times that is 1.85 m/s, above the 1.57 m/s limit.
        Violations(Reg(0.8, 0.30, 2.0) with { SignalModPct = 100 }).Should().NotContain("VELOCITY");
        Violations(Reg(0.8, 0.30, 2.0) with { SignalModPct = 400 }).Should().Contain("VELOCITY");
    }

    [Fact]
    public void Without_an_acceleration_limit_nothing_can_be_sent()
    {
        var noAccel = Cfg with { Limits = Cfg.Limits with { MaxAccelMs2 = null } };
        var r = new WaveMathEngine(noAccel).Calculate(Reg());
        r.Safety.Ok.Should().BeFalse();
        r.Safety.Violations.Select(v => v.Code).Should().Contain("ACCEL_NOT_CONFIGURED");
    }

    [Fact]
    public void A_safe_wave_has_no_violations_and_an_unsafe_one_has_a_readable_message()
    {
        new WaveMathEngine(Cfg).Calculate(Reg(0.6, 0.05, 1.0)).Safety.Ok.Should().BeTrue();
        var bad = new WaveMathEngine(Cfg).Calculate(Reg(0.6, 0.30, 0.6)).Safety;
        bad.Ok.Should().BeFalse();
        bad.Violations.Should().OnlyContain(v => v.Message.Length > 10 && v.Code.Length > 3);
    }

    // ---- the Limit curve on screen must agree with what the gatekeeper blocks ---------------

    [Theory]
    [InlineData(0.6, 0.6)] [InlineData(0.6, 0.8)] [InlineData(0.6, 1.0)] [InlineData(0.6, 1.5)] [InlineData(0.6, 2.0)]
    [InlineData(0.6, 3.0)] [InlineData(0.6, 4.0)] [InlineData(0.6, 6.0)] [InlineData(0.3, 1.0)] [InlineData(0.8, 1.5)]
    public void Limit_curve_agrees_with_the_gatekeeper(double depth, double period)
    {
        var limit = new LimitCurve(Cfg).Evaluate(period, depth);
        var below = Violations(Reg(depth, limit.HeightM * 0.97, period));
        below.Should().BeEmpty($"3 % under the limit at T={period}, h={depth} must be allowed (limit {limit.HeightM:0.0000} m, {limit.Active})");
        var above = Violations(Reg(depth, limit.HeightM * 1.03, period));
        above.Should().NotBeEmpty($"3 % over the limit at T={period}, h={depth} must be blocked (limit {limit.HeightM:0.0000} m, {limit.Active})");
    }

    // ---- random waves at the edges ------------------------------------------------------------

    [Theory]
    [InlineData(SpectralShape.Jonswap)] [InlineData(SpectralShape.GeneralizedPm)]
    public void Random_waves_obey_the_same_depth_height_and_range_rules(SpectralShape shape)
    {
        var ok = new WaveRequest { Type = WaveType.Random, Spectrum = shape, WaveHeightM = 0.04, PeriodS = 1.5, NumWaves = 100, RampS = 4 };
        Violations(ok).Should().BeEmpty();
        Violations(ok with { WaterDepthM = 0.9 }).Should().Contain("DEPTH_RANGE");
        Violations(ok with { WaveHeightM = 0.4 }).Should().Contain("HEIGHT_RANGE");
        Violations(ok with { NumWaves = 9 }).Should().Contain("NUM_WAVES_RANGE");
        Violations(ok with { SamplingHz = 40 }).Should().Contain("FS_RANGE");
    }

    [Theory]
    [InlineData(SpectralShape.Jonswap, 0.25, "PERIOD_FLOOR")] [InlineData(SpectralShape.Jonswap, 0.45, "PERIOD_RANGE")] [InlineData(SpectralShape.Jonswap, 0.59, "PERIOD_RANGE")]
    [InlineData(SpectralShape.Jonswap, 6.01, "PERIOD_RANGE")] [InlineData(SpectralShape.Jonswap, 8.0, "PERIOD_RANGE")]
    [InlineData(SpectralShape.GeneralizedPm, 0.45, "PERIOD_RANGE")] [InlineData(SpectralShape.GeneralizedPm, 8.0, "PERIOD_RANGE")]
    public void Random_wave_with_a_peak_period_outside_the_wavemaker_range_is_blocked(SpectralShape shape, double tp, string code)
    {
        // Before this check such a sea was accepted although up to 98 % of its energy was cut away.
        var req = new WaveRequest { Type = WaveType.Random, Spectrum = shape, WaveHeightM = 0.03, PeriodS = tp, NumWaves = 100, RampS = 4 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeFalse();
        r.Safety.Violations.Select(v => v.Code).Should().Contain(code);
    }

    [Theory]
    [InlineData(SpectralShape.Jonswap, 0.6, true)] [InlineData(SpectralShape.Jonswap, 0.8, true)] [InlineData(SpectralShape.Jonswap, 1.0, true)]
    [InlineData(SpectralShape.Jonswap, 1.5, false)] [InlineData(SpectralShape.Jonswap, 3.0, false)] [InlineData(SpectralShape.Jonswap, 6.0, true)]
    [InlineData(SpectralShape.GeneralizedPm, 0.7, true)] [InlineData(SpectralShape.GeneralizedPm, 1.5, false)] [InlineData(SpectralShape.GeneralizedPm, 6.0, true)]
    public void Random_wave_that_loses_much_of_its_energy_to_the_band_limits_gets_a_warning(SpectralShape shape, double tp, bool warned)
    {
        var req = new WaveRequest { Type = WaveType.Random, Spectrum = shape, WaveHeightM = 0.03, PeriodS = tp, NumWaves = 100, RampS = 4 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)), "the wave itself is safe");
        r.Safety.Warnings.Select(w => w.Code).Contains("BAND_LIMITED").Should().Be(warned);
        (r.Random!.EnergyRemovedPct > IrregularWaves.BandLimitWarnPct).Should().Be(warned);
    }

    [Fact]
    public void Band_limited_warning_text_says_how_much_is_lost()
    {
        var req = new WaveRequest { Type = WaveType.Random, WaveHeightM = 0.03, PeriodS = 0.8, NumWaves = 100, RampS = 4 };
        var text = new WaveMathEngine(Cfg).Calculate(req).Safety.Warnings.Single(w => w.Code == "BAND_LIMITED").Message;
        text.Should().Contain("%").And.Contain("lower than requested");
        var scaled = new WaveMathEngine(Cfg).Calculate(req with { ScaleToTargetHs = true }).Safety.Warnings.Single(w => w.Code == "BAND_LIMITED").Message;
        scaled.Should().Contain("scaled up");
    }
}
