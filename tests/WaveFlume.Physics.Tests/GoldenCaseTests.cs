using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

/// <summary>Legacy IITM WaveGen screenshot, spec §14. Tolerance 1 %.</summary>
public class GoldenCaseTests
{
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 5.0 } };
    private static readonly WaveRequest Req = new() { WaterDepthM = 0.6, WaveHeightM = 0.05, PeriodS = 1.0 };

    private static void Near(double? actual, double expected) =>
        actual.Should().BeApproximately(expected, Math.Abs(expected) * 0.01);

    [Fact]
    public void Matches_legacy_screenshot()
    {
        var r = new WaveMathEngine(Cfg).Calculate(Req);
        var k = r.Kinematics!;

        Near(k.K, 4.085);
        Near(k.WaveLengthM, 1.538);
        Near(k.CelerityMs, 1.538);
        Near(r.Wavemaker.C0, 1.838);
        Near(r.Wavemaker.StrokeAmplitudeM, 0.0136);
        Near(k.UMaxMs, 0.15708);
        Near(r.Wavemaker.PeakVelocityMs, 0.0855);
        Near(r.Wavemaker.PeakAccelMs2, 0.537);
        Near(r.Wavemaker.PowerW, 7.6);
        Near(r.Wavemaker.ForceEstimateN, 177);
        k.Regime.Should().Be(DepthRegime.Intermediate);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
    }

    [Fact]
    public void Trajectory_peak_matches_formula_and_has_six_paddles()
    {
        var r = new WaveMathEngine(Cfg).Calculate(Req);
        r.Trajectory.PaddleCount.Should().Be(6);
        r.Trajectory.SampleCount.Should().Be(10 * 125 + 1);
        r.Safety.Worst.MaxAbsPositionM.Should().BeApproximately(r.Wavemaker.StrokeAmplitudeM, 1e-4);
        r.Safety.Worst.MaxAbsVelocityMs.Should().BeApproximately(r.Wavemaker.PeakVelocityMs, 0.003);
    }

    [Fact]
    public void Signal_modification_scales_trajectory()
    {
        var r = new WaveMathEngine(Cfg).Calculate(Req with { SignalModPct = 50 });
        r.Safety.Worst.MaxAbsPositionM.Should().BeApproximately(0.5 * 0.0136, 1e-4);
    }

    [Fact]
    public void Bichromatic_sums_components()
    {
        var r = new WaveMathEngine(Cfg).Calculate(Req with { Type = WaveType.Bichromatic, Height2M = 0.03, Period2S = 1.2 });
        r.Components.Should().HaveCount(2);
        r.Wavemaker.StrokeAmplitudeM.Should().BeApproximately(r.Components.Sum(c => c.StrokeAmplitudeM), 1e-12);
        r.Safety.Worst.MaxAbsPositionM.Should().BeLessThanOrEqualTo(r.Wavemaker.StrokeAmplitudeM + 1e-6);
    }

    [Fact]
    public void Unimplemented_paths_throw_clearly()
    {
        var engine = new WaveMathEngine(Cfg);
        engine.Invoking(e => e.Calculate(Req with { Order = WaveOrder.Second, Type = WaveType.Bichromatic, Height2M = 0.02, Period2S = 1.3 }))
            .Should().Throw<NotImplementedException>().WithMessage("*Schäffer*");
        engine.Invoking(e => e.Calculate(Req with { Type = WaveType.Bichromatic })).Should().Throw<ArgumentException>();
        engine.Invoking(e => e.Calculate(Req with { Type = WaveType.UserDefinedWave })).Should().Throw<ArgumentException>();
        engine.Invoking(e => e.Calculate(Req with { SignalModPct = 0 })).Should().Throw<ArgumentOutOfRangeException>();
        new WaveMathEngine(Cfg with { PaddleType = PaddleType.Flap, HingeHeightM = 0 })
            .Invoking(e => e.Calculate(Req with { Order = WaveOrder.Second })).Should().Throw<NotImplementedException>().WithMessage("*piston*");
    }
}
