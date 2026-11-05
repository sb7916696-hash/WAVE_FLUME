using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using WaveFlume.Core;
using WaveFlume.Physics;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

/// <summary>
/// Physics generators checked through the simulator, whose probe model (servo lag + Biesel transfer +
/// propagation, Phase 2) is independent of the generator code.
/// </summary>
public class EndToEndWaveTests
{
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 20 } };

    private static async Task<List<TelemetrySnapshot>> RunThroughSimulator(Trajectory traj, double probeX, double seconds)
    {
        var time = new FakeTimeProvider();
        var sim = new SimulatorDriver(new SimulatorOptions { Flume = Cfg, ProbeXM = [probeX], ProbeNoiseM = 0, MaxBlockWords = 4800 }, time);
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

    [Fact]
    public async Task Focused_group_peaks_at_the_focus_position_and_time()
    {
        var req = new WaveRequest { Type = WaveType.Focusing, WaveHeightM = 0.06, PeriodS = 1.5, NumWaves = 40, RampS = 5, FocusXM = 10, FocusTimeS = 30 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));

        var samples = await RunThroughSimulator(r.Trajectory, req.FocusXM, 40);
        var peak = samples.MaxBy(s => s.ProbeM[0])!;
        peak.TimeS.Should().BeApproximately(req.FocusTimeS, 0.3);
        ((double)peak.ProbeM[0]).Should().BeInRange(0.85 * 0.03, 1.05 * 0.03, "linear crest A_f = H/2");
    }

    [Fact]
    public async Task Random_sea_at_a_probe_has_the_realised_hm0()
    {
        var req = new WaveRequest { Type = WaveType.Random, WaveHeightM = 0.05, PeriodS = 1.5, NumWaves = 150, RampS = 5 };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        var samples = await RunThroughSimulator(r.Trajectory, 5, 225);
        var eta = samples.Where(s => s.TimeS is > 30 and < 220).Select(s => (double)s.ProbeM[0]).ToArray();
        double hm0 = WaveStatistics.Spectral(eta, 125).Hm0M;
        hm0.Should().BeApproximately(r.Random!.RealisedHm0M, 0.06 * r.Random.RealisedHm0M);
        WaveStatistics.Spectral(eta, 125, 8192).TpS.Should().BeApproximately(1.5, 0.2);
    }
}
