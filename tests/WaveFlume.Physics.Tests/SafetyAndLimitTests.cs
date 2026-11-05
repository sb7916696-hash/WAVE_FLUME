using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

public class SafetyAndLimitTests
{
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 50 } };

    /// <summary>Six identical ramped sines of amplitude <paramref name="amp"/> at period <paramref name="t"/>.</summary>
    private static Trajectory Sine(double amp, double t, double fs = 125, double duration = 20, double ramp = 2)
    {
        int n = (int)(duration * fs) + 1;
        var row = new float[n];
        for (int i = 0; i < n; i++)
            row[i] = (float)(amp * Ramp.Envelope(i / fs, duration, ramp) * Math.Sin(2 * Math.PI * i / fs / t));
        return new Trajectory(Enumerable.Repeat(row, 6).ToArray(), fs);
    }

    private static IEnumerable<string> Codes(SafetyReport r) => r.Violations.Select(v => v.Code);

    [Fact]
    public void Rejects_period_below_floor()
    {
        var cfg = Cfg with { Ranges = new RangeBounds { PeriodS = new(0.3, 6) }, Limits = Cfg.Limits with { TMinS = 0.3 } };
        var c = WaveComponent.Create(0.01, 0.29, 0.6, cfg);
        var r = new SafetyGatekeeper(cfg).Validate(new WaveRequest { PeriodS = 0.29 }, [c], Sine(0.001, 0.29));
        Codes(r).Should().Contain("PERIOD_FLOOR");
    }

    [Fact]
    public void Rejects_stroke_above_clamp()
    {
        var r = new SafetyGatekeeper(Cfg).Validate(new WaveRequest(), [], Sine(0.46, 6));
        Codes(r).Should().Contain("STROKE");
    }

    [Fact]
    public void Rejects_velocity_above_limit()
    {
        double amp = 1.6 / (2 * Math.PI); // 1.6 m/s at T = 1 s, inside the stroke clamp
        var r = new SafetyGatekeeper(Cfg).Validate(new WaveRequest(), [], Sine(amp, 1));
        Codes(r).Should().Contain("VELOCITY").And.NotContain("STROKE");
    }

    [Fact]
    public void Rejects_missing_acceleration_limit()
    {
        var r = new SafetyGatekeeper(new FlumeConfig()).Validate(new WaveRequest(), [], Sine(0.01, 2));
        Codes(r).Should().Contain("ACCEL_NOT_CONFIGURED");
    }

    [Fact]
    public void Rejects_acceleration_above_limit()
    {
        var cfg = Cfg with { Limits = Cfg.Limits with { MaxAccelMs2 = 1.0 } };
        var r = new SafetyGatekeeper(cfg).Validate(new WaveRequest(), [], Sine(0.05, 1)); // ~2 m/s²
        Codes(r).Should().Contain("ACCELERATION");
    }

    [Fact]
    public void Rejects_nonzero_start_and_low_sampling_rate()
    {
        var traj = Sine(0.01, 1, ramp: 0);
        traj.PositionsM[0] = traj.PositionsM[0].Select(x => x + 0.01f).ToArray();
        var c = WaveComponent.Create(0.05, 1.0, 0.6, Cfg);
        var r = new SafetyGatekeeper(Cfg).Validate(new WaveRequest { SamplingHz = 5 }, [c], traj);
        Codes(r).Should().Contain("START_NOT_ZERO").And.Contain("FS_TOO_LOW").And.Contain("FS_RANGE");
    }

    [Fact]
    public void Phase_step_warns_then_rejects()
    {
        var engine = new WaveMathEngine(Cfg);
        // T = 0.6 s → k ≈ 11.2; k b = 5.6 rad. sin θ for 90° step ≈ 0.28, for 180° ≈ 0.56.
        var baseReq = new WaveRequest { PeriodS = 0.6, WaveHeightM = 0.02, NumWaves = 20, RampS = 1 };
        var warn = engine.Calculate(baseReq with { AngleDeg = 20 }).Safety;
        warn.Warnings.Select(w => w.Code).Should().Contain("PHASE_STEP");
        Codes(warn).Should().NotContain("PHASE_STEP");
        var reject = engine.Calculate(baseReq with { AngleDeg = 40 }).Safety;
        Codes(reject).Should().Contain("PHASE_STEP");
    }

    [Fact]
    public void Rejects_breaking_wave()
    {
        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest { PeriodS = 0.6, WaveHeightM = 0.25, NumWaves = 20, RampS = 1 });
        Codes(r.Safety).Should().Contain("BREAKING");
    }

    [Fact]
    public void Config_rejects_raising_ceilings()
    {
        new FlumeConfig { Limits = new OperationalLimits { MaxStrokeM = 0.46 } }.Validate().Should().NotBeEmpty();
        new FlumeConfig { Limits = new OperationalLimits { MaxVelocityMs = 1.6 } }.Validate().Should().NotBeEmpty();
        new FlumeConfig { Limits = new OperationalLimits { TMinS = 0.29 } }.Validate().Should().NotBeEmpty();
        new FlumeConfig().Validate().Should().BeEmpty();
    }

    [Fact]
    public void Effective_period_and_depth_bounds()
    {
        var cfg = new FlumeConfig();
        cfg.EffectiveTMinS.Should().Be(0.6);
        cfg.EffectiveDepthRange.Max.Should().Be(0.8);
        (cfg with { Limits = cfg.Limits with { TMinS = 0.3 }, Ranges = new RangeBounds { PeriodS = new(0.1, 6) } })
            .EffectiveTMinS.Should().Be(HardLimits.AbsoluteMinPeriodS);
    }

    [Fact]
    public void Repo_config_file_loads()
    {
        var cfg = FlumeConfig.Load(Path.Combine(AppContext.BaseDirectory, "flume_config.json"));
        cfg.Should().BeEquivalentTo(new FlumeConfig());
    }

    [Fact]
    public void Limit_curve_has_breaking_left_and_kinematic_right()
    {
        var curve = new LimitCurve(Cfg).Compute(0.6, 100);
        curve.First().PeriodS.Should().Be(Cfg.EffectiveTMinS);
        curve.Last().PeriodS.Should().Be(Cfg.EffectiveTMaxS);
        curve.First().Active.Should().Be(LimitKind.Breaking);
        curve.Last().Active.Should().BeOneOf(LimitKind.Stroke, LimitKind.Velocity, LimitKind.Acceleration);
        curve.Should().OnlyContain(p => p.HeightM > 0 && p.HeightM <= Cfg.Ranges.WaveHeightM.Max);
    }

    [Fact]
    public void Limit_curve_inside_outside()
    {
        var lc = new LimitCurve(Cfg);
        lc.IsInside(0.05, 1.0, 0.6).Should().BeTrue();
        lc.IsInside(0.29, 0.7, 0.6).Should().BeFalse();
        lc.IsInside(0.05, 0.5, 0.6).Should().BeFalse("below effective T_min");
    }

    [Fact]
    public void Limit_curve_stroke_term_matches_formula()
    {
        var cfg = Cfg with { Limits = Cfg.Limits with { MaxAccelMs2 = 1e6 }, Ranges = new RangeBounds { WaveHeightM = new(0.01, 10) } };
        var p = new LimitCurve(cfg).Evaluate(6.0, 0.6);
        var w = Dispersion.Solve(6.0, 0.6, 9.81);
        double hStroke = 2 * TransferFunctions.Piston(w.Kh) * 0.35;
        double hVel = 2 * TransferFunctions.Piston(w.Kh) * 1.57 / w.Omega;
        p.HeightM.Should().BeApproximately(Math.Min(hStroke, Math.Min(hVel, LimitCurve.BreakingHeight(w))), 1e-12);
    }
}
