using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using WaveFlume.Core;
using WaveFlume.Physics;
using WaveFlume.Plc;
using Xunit.Abstractions;

namespace WaveFlume.Plc.Tests;

/// <summary>
/// End to end: request -> calculation -> safety check -> chunked upload -> paddle control -> simulated flume ->
/// probe readings. The simulator's wave model (servo lag, Biesel transfer function, propagation) is written
/// independently of the wave generators, so the measured wave is a genuine check of the generated motion.
/// </summary>
public class MonochromaticAndRandomEndToEndTests(ITestOutputHelper output)
{
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 20 } };

    private static async Task<List<TelemetrySnapshot>> Run(Trajectory traj, double depth, double probeX, double seconds)
    {
        var time = new FakeTimeProvider();
        var sim = new SimulatorDriver(new SimulatorOptions { Flume = Cfg, WaterDepthM = depth, ProbeXM = [probeX], ProbeNoiseM = 0, MaxBlockWords = 4800 }, time);
        await sim.ConnectAsync(new PlcEndpoint(), default);
        (await TrajectoryChunker.UploadAsync(sim, traj, TrajectoryChunker.ValuesPerChunk(4800, 2, int.MaxValue, 6), null, default))
            .Should().BeTrue(sim.LastError);
        await sim.SendControlCommandAsync(PlcControlCommand.Enable, default);
        await sim.SendControlCommandAsync(PlcControlCommand.Home, default);
        (await sim.SendControlCommandAsync(PlcControlCommand.Start, default)).Should().BeTrue(sim.LastError);
        var samples = new List<TelemetrySnapshot>();
        for (double t = 0; t < seconds; t += 0.2)
        {
            time.Advance(TimeSpan.FromSeconds(0.2));
            await sim.SendHeartbeatAsync(1, default);
            samples.AddRange((await sim.ReadTelemetryBlockAsync(default)).Samples);
        }
        long start = samples[0].SampleCounter;
        return samples.Select(s => s with { TimeS = (s.SampleCounter - start) / 125.0 }).ToList();
    }

    [Theory]
    [InlineData(0.6, 0.05, 1.0)]
    [InlineData(0.4, 0.03, 2.0)]
    [InlineData(0.8, 0.10, 1.5)]
    [InlineData(0.6, 0.04, 0.8)]
    [InlineData(0.5, 0.06, 3.0)]
    public async Task Monochromatic_wave_is_measured_at_the_probe_with_the_requested_height_and_period(double depth, double height, double period)
    {
        var req = new WaveRequest { WaterDepthM = depth, WaveHeightM = height, PeriodS = period, NumWaves = (int)(40 / period) + 10, RampS = 3 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));

        double duration = req.NumWaves * period;
        var samples = await Run(r.Trajectory, req.WaterDepthM, 5, duration + 25);
        var eta = samples.Where(s => s.TimeS > 20 && s.TimeS < duration - 3).Select(s => (double)s.ProbeM[0]).ToArray();
        var stats = WaveStatistics.ZeroUpcrossing(eta, 125);
        output.WriteLine($"h={depth} H={height} T={period}: measured H1/3={stats.H13M:0.0000} Hmean-ish max={stats.HmaxM:0.0000} Tz={stats.TzS:0.000} waves={stats.WaveCount}");

        stats.H13M.Should().BeApproximately(height, 0.10 * height, "the measured wave height is the requested one");
        stats.TzS.Should().BeApproximately(period, 0.02 * period, "the measured period is the requested one");
    }

    [Fact]
    public async Task Monochromatic_wave_height_scales_with_the_requested_height()
    {
        double Measure(WaveRequest req, IReadOnlyList<TelemetrySnapshot> s, double duration) =>
            WaveStatistics.ZeroUpcrossing(s.Where(x => x.TimeS > 20 && x.TimeS < duration - 3).Select(x => (double)x.ProbeM[0]).ToArray(), 125).H13M;

        var a = new WaveRequest { WaveHeightM = 0.03, NumWaves = 50, RampS = 3 };
        var b = a with { WaveHeightM = 0.06 };
        var sa = await Run(new WaveMathEngine(Cfg).Calculate(a).Trajectory, a.WaterDepthM, 5, 75);
        var sb = await Run(new WaveMathEngine(Cfg).Calculate(b).Trajectory, b.WaterDepthM, 5, 75);
        (Measure(b, sb, 50) / Measure(a, sa, 50)).Should().BeApproximately(2.0, 0.05);
    }

    public static IEnumerable<object[]> Seas() =>
    [
        [SpectralShape.Jonswap, 3.3, 1.25, 1.5], [SpectralShape.Jonswap, 1.0, 1.25, 1.5], [SpectralShape.Jonswap, 3.3, 1.25, 2.5],
        [SpectralShape.GeneralizedPm, 3.3, 1.25, 1.5], [SpectralShape.GeneralizedPm, 3.3, 2.0, 2.0],
        [SpectralShape.PiersonMoskowitz, 3.3, 1.25, 1.5], [SpectralShape.Tma, 3.3, 1.25, 1.5], [SpectralShape.Darbyshire, 3.3, 1.25, 1.5],
    ];

    [Theory]
    [MemberData(nameof(Seas))]
    public async Task Random_sea_is_measured_at_the_probe_with_the_requested_hs_and_peak_period(SpectralShape shape, double gamma, double beta, double tp)
    {
        var req = new WaveRequest
        {
            Type = WaveType.Random, Spectrum = shape, Gamma = gamma, Beta = beta, WaveHeightM = 0.05, PeriodS = tp,
            NumWaves = (int)(250 / tp), RampS = 5, ScaleToTargetHs = true, RandomSeed = 777,
        };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));

        double duration = req.NumWaves * tp;
        var samples = await Run(r.Trajectory, req.WaterDepthM, 5, duration + 25);
        var eta = samples.Where(s => s.TimeS > 25 && s.TimeS < duration - 3).Select(s => (double)s.ProbeM[0]).ToArray();
        var spec = WaveStatistics.Spectral(eta, 125, 8192);
        double fpk = shape == SpectralShape.GeneralizedPm ? Math.Pow(4 * beta / 5, 0.25) / tp : 1 / tp;
        output.WriteLine($"{shape} gamma={gamma} beta={beta} Tp={tp}: Hm0={spec.Hm0M:0.0000} (target 0.05, generator predicted {r.Random!.RealisedHm0M:0.0000}) peak {1 / spec.TpS:0.000} Hz (expected {fpk:0.000})");

        spec.Hm0M.Should().BeApproximately(r.Random!.RealisedHm0M, 0.08 * r.Random.RealisedHm0M, "what the probe sees is what the generator predicted");
        spec.Hm0M.Should().BeApproximately(0.05, 0.15 * 0.05, "and that is close to the requested Hs");
        (1 / spec.TpS).Should().BeApproximately(fpk, 0.25 * fpk);
    }

    [Fact]
    public async Task Random_sea_has_the_same_motion_every_time_the_same_seed_is_run()
    {
        var req = new WaveRequest { Type = WaveType.Random, WaveHeightM = 0.04, PeriodS = 1.5, NumWaves = 60, RampS = 4, RandomSeed = 5 };
        async Task<double[]> Measure(WaveResult r) => (await Run(r.Trajectory, req.WaterDepthM, 5, 110)).Select(s => (double)s.ProbeM[0]).ToArray();
        var a = await Measure(new WaveMathEngine(Cfg).Calculate(req));
        var b = await Measure(new WaveMathEngine(Cfg).Calculate(req));
        a.Length.Should().Be(b.Length);
        a.Zip(b, (x, y) => Math.Abs(x - y)).Max().Should().BeLessThan(1e-6);
    }

    [Fact]
    public async Task Bichromatic_wave_is_measured_with_both_component_periods_present()
    {
        var req = new WaveRequest { Type = WaveType.Bichromatic, WaveHeightM = 0.04, PeriodS = 1.5, Height2M = 0.04, Period2S = 1.8, NumWaves = 60, RampS = 3 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        double duration = req.NumWaves * req.PeriodS;
        var samples = await Run(r.Trajectory, req.WaterDepthM, 5, duration + 25);
        var eta = samples.Where(s => s.TimeS > 20 && s.TimeS < duration - 3).Select(s => (double)s.ProbeM[0]).ToArray();
        var spec = WaveStatistics.Spectral(eta, 125, 8192);
        output.WriteLine($"Bichromatic: Hm0={spec.Hm0M:0.0000} Tp={spec.TpS:0.000}");
        // Two equal 0.04 m components: variance = 2*(0.02^2/2), so Hm0 = 4*sqrt(0.0004) = 0.08 m.
        spec.Hm0M.Should().BeApproximately(0.08, 0.15 * 0.08);
        var peak = eta.Max() - eta.Min();
        peak.Should().BeGreaterThan(0.06, "the two waves add up at the group maximum");
    }

    [Fact]
    public async Task Cnoidal_wave_runs_through_the_flume_with_the_requested_period()
    {
        var req = new WaveRequest { Type = WaveType.Cnoidal, WaterDepthM = 0.5, WaveHeightM = 0.03, PeriodS = 4.0, NumWaves = 15, RampS = 3 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        double duration = req.NumWaves * req.PeriodS;
        var samples = await Run(r.Trajectory, req.WaterDepthM, 5, duration + 25);
        var eta = samples.Where(s => s.TimeS > 15 && s.TimeS < duration - 3).Select(s => (double)s.ProbeM[0]).ToArray();
        var stats = WaveStatistics.ZeroUpcrossing(eta, 125);
        output.WriteLine($"Cnoidal: H1/3={stats.H13M:0.0000} Tz={stats.TzS:0.000} waves={stats.WaveCount}");
        stats.WaveCount.Should().BeGreaterThan(5);
        stats.TzS.Should().BeApproximately(req.PeriodS, 0.10 * req.PeriodS);
        stats.H13M.Should().BeGreaterThan(0.015).And.BeLessThan(0.06);
    }}
