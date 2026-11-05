using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using WaveFlume.Core;
using WaveFlume.Physics;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

public class SimulatorAndTelemetryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>Golden-case wave: h 0.6, H 0.05, T 1 → X_a ≈ 13.6 mm, all paddles in phase.</summary>
    private static async Task<(SimulatorDriver Sim, FakeTimeProvider Time, double XaMm)> StartedRun(SimulatorOptions? options = null)
    {
        var opt = options ?? new SimulatorOptions { ProbeNoiseM = 0 };
        var time = new FakeTimeProvider();
        var sim = new SimulatorDriver(opt, time);
        await sim.ConnectAsync(new PlcEndpoint(), Ct);
        double xa = Units.MToMm(WaveComponent.Create(0.05, 1.0, 0.6, opt.Flume).StrokeAmplitudeM);

        (await sim.SendControlCommandAsync(PlcControlCommand.Enable, Ct)).Should().BeTrue();
        (await sim.SendControlCommandAsync(PlcControlCommand.Home, Ct)).Should().BeTrue();
        (await sim.SendWaveRecipeAsync(new WaveCommandPacket(1.0, 2.0, 20, Enumerable.Repeat(xa, 6).ToArray(), new double[6]), Ct)).Should().BeTrue();
        (await sim.SendControlCommandAsync(PlcControlCommand.Start, Ct)).Should().BeTrue(sim.LastError);
        return (sim, time, xa);
    }

    private static async Task<List<TelemetrySnapshot>> RunFor(SimulatorDriver sim, FakeTimeProvider time, double seconds, bool heartbeat = true)
    {
        var samples = new List<TelemetrySnapshot>();
        for (double t = 0; t < seconds; t += 0.04)
        {
            time.Advance(TimeSpan.FromSeconds(0.04));
            if (heartbeat) await sim.SendHeartbeatAsync(1, Ct);
            samples.AddRange((await sim.ReadTelemetryBlockAsync(Ct)).Samples);
        }
        return samples;
    }

    [Fact]
    public async Task Run_produces_paddle_motion_and_biesel_probe_waves()
    {
        var (sim, time, xa) = await StartedRun();
        var samples = await RunFor(sim, time, 15);

        samples.Select(s => s.SampleCounter).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        ((StatusBits)samples[^1].StatusWord).Should().HaveFlag(StatusBits.Running);

        var steady = samples.Where(s => s.TimeS is > 4 and < 15).ToList();
        steady.Max(s => (double)Math.Abs(s.PaddleMm[0])).Should().BeApproximately(xa, 0.03 * xa);
        steady.Max(s => Math.Abs(s.PaddleMm[5] - s.PaddleMm[0])).Should().BeLessThan(0.01f, "zero phase → paddles move together");

        // Probe 1 at 5 m: wave arrives after ~x/Cg ≈ 6 s, then η amplitude ≈ H/2.
        var probe = samples.Where(s => s.TimeS is > 10 and < 15).Select(s => (double)s.ProbeM[0]).ToList();
        ((probe.Max() - probe.Min()) / 2).Should().BeApproximately(0.025, 0.025 * 0.05);
        samples.Where(s => s.TimeS < 2).Max(s => Math.Abs(s.ProbeM[0])).Should().BeLessThan(0.002f, "propagation delay");
    }

    [Fact]
    public async Task Heartbeat_loss_triggers_smooth_stop_in_simulator()
    {
        var (sim, time, _) = await StartedRun();
        await RunFor(sim, time, 5);

        var during = await RunFor(sim, time, 2, heartbeat: false);  // timeout 1 s, smooth stop 2 s
        var last = during[^1];
        ((StatusBits)last.StatusWord).Should().HaveFlag(StatusBits.SmoothStopping).And.HaveFlag(StatusBits.Fault);
        ((FaultBits)last.FaultWord).Should().HaveFlag(FaultBits.CommsLoss);

        var after = await RunFor(sim, time, 2, heartbeat: false);
        ((StatusBits)after[^1].StatusWord).Should().NotHaveFlag(StatusBits.Running);
        after[^1].PaddleMm.Should().OnlyContain(x => x == 0);
        (await sim.SendControlCommandAsync(PlcControlCommand.Start, Ct)).Should().BeFalse("fault latched until Reset");
        (await sim.SendControlCommandAsync(PlcControlCommand.Reset, Ct)).Should().BeTrue();
        (await sim.SendControlCommandAsync(PlcControlCommand.Start, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Estop_holds_and_needs_reset_and_enable()
    {
        var (sim, time, _) = await StartedRun();
        await RunFor(sim, time, 3);
        (await sim.SendControlCommandAsync(PlcControlCommand.EStop, Ct)).Should().BeTrue();
        var s = await sim.PollTelemetryAsync(Ct);
        ((StatusBits)s.StatusWord).Should().HaveFlag(StatusBits.EStopActive).And.NotHaveFlag(StatusBits.Running);
        (await sim.SendControlCommandAsync(PlcControlCommand.Enable, Ct)).Should().BeFalse();
        await sim.SendControlCommandAsync(PlcControlCommand.Reset, Ct);
        (await sim.SendControlCommandAsync(PlcControlCommand.Enable, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Simulator_enforces_stroke_clamp_and_start_preconditions()
    {
        var time = new FakeTimeProvider();
        var sim = new SimulatorDriver(new SimulatorOptions(), time);
        await sim.ConnectAsync(new PlcEndpoint(), Ct);
        (await sim.SendControlCommandAsync(PlcControlCommand.Start, Ct)).Should().BeFalse("not enabled/homed/loaded");
        (await sim.SendWaveRecipeAsync(new WaveCommandPacket(2, 1, 10, Enumerable.Repeat(400.0, 6).ToArray(), new double[6]), Ct))
            .Should().BeFalse("400 mm exceeds the 350 mm clamp");
        ((FaultBits)(await sim.PollTelemetryAsync(Ct)).FaultWord).Should().HaveFlag(FaultBits.StrokeLimit);
    }

    [Fact]
    public async Task Block_reads_report_lost_samples_when_ring_overflows()
    {
        var time = new FakeTimeProvider();
        var sim = new SimulatorDriver(new SimulatorOptions { RingCapacitySamples = 100 }, time);
        await sim.ConnectAsync(new PlcEndpoint(), Ct);
        await sim.ReadTelemetryBlockAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(2)); // 250 samples > 100 ring
        var block = await sim.ReadTelemetryBlockAsync(Ct);
        block.Samples.Should().HaveCount(100);
        block.LostSamples.Should().Be(150);
    }

    // ---- PlcTelemetryService -------------------------------------------------------------------

    [Fact]
    public async Task Service_feeds_ui_buffer_and_csv_channel_without_loss()
    {
        var (sim, time, _) = await StartedRun();
        var svc = new PlcTelemetryService(sim, new TelemetryOptions(), time);
        var reader = svc.BeginCsvCapture();
        for (int i = 0; i < 25; i++)
        {
            time.Advance(TimeSpan.FromSeconds(0.04));
            await svc.TickAsync(Ct);
        }
        int csv = 0;
        while (reader.TryRead(out _)) csv++;
        svc.EndCsvCapture();
        reader.Completion.IsCompleted.Should().BeTrue();
        csv.Should().BeInRange(124, 126);
        svc.UiBuffer.Latest(1000).Should().HaveCount(csv);
        svc.Timing.LostSamples.Should().Be(0);
        ((StatusBits)svc.UiBuffer.Latest(1)[0].StatusWord).Should().HaveFlag(StatusBits.Running, "service heartbeats keep the run alive");
    }

    [Fact]
    public async Task Service_smooth_stops_on_plc_heartbeat_freeze()
    {
        var (sim, time, _) = await StartedRun();
        var spy = new SpyDriver(sim) { Mutate = s => s with { Heartbeat = 42 } };
        var svc = new PlcTelemetryService(spy, new TelemetryOptions { HeartbeatTimeoutS = 1 }, time);
        var alerts = new List<Alert>();
        svc.AlertRaised += (_, a) => alerts.Add(a);

        for (int i = 0; i < 40; i++) { time.Advance(TimeSpan.FromSeconds(0.04)); await svc.TickAsync(Ct); }

        alerts.Should().ContainSingle(a => a.Severity == AlertSeverity.Critical && a.Source == AlertSources.Comms);
        spy.Commands.Should().Equal(PlcControlCommand.SmoothStop);
    }

    [Fact]
    public async Task Service_guards_position_and_hard_faults()
    {
        var (sim, time, _) = await StartedRun();
        var spy = new SpyDriver(sim);
        var svc = new PlcTelemetryService(spy, new TelemetryOptions { StrokeClampM = 0.01 }, time); // 9.5 mm guard < 13.6 mm motion
        var alerts = new List<Alert>();
        svc.AlertRaised += (_, a) => alerts.Add(a);
        for (int i = 0; i < 150; i++) { time.Advance(TimeSpan.FromSeconds(0.04)); await svc.TickAsync(Ct); }
        alerts.Should().Contain(a => a.Source == AlertSources.ServoDrive && a.Severity == AlertSeverity.Critical);
        spy.Commands.Should().Contain(PlcControlCommand.SmoothStop);

        spy.Mutate = s => s with { FaultWord = (ushort)FaultBits.DriveFault };
        time.Advance(TimeSpan.FromSeconds(0.04));
        await svc.TickAsync(Ct);
        alerts.Should().Contain(a => a.Severity == AlertSeverity.EStop);
        spy.Commands.Should().EndWith(PlcControlCommand.EStop);
    }

    [Fact]
    public async Task Service_converts_raw_probe_current_and_trips_on_loop_faults()
    {
        var (sim, time, _) = await StartedRun();
        var spy = new SpyDriver(sim);
        var svc = new PlcTelemetryService(spy, new TelemetryOptions { ActiveProbes = 2 }, time)
        {
            ProbeConverter = raw => Array.ConvertAll(raw, ma => ma * 100f), // recognisable test conversion
        };
        var alerts = new List<Alert>();
        svc.AlertRaised += (_, a) => alerts.Add(a);

        time.Advance(TimeSpan.FromSeconds(0.04));
        await svc.TickAsync(Ct);
        var s = svc.UiBuffer.Latest(1)[0];
        s.ProbeM[0].Should().BeApproximately(s.ProbeRawMa[0] * 100f, 1e-3f);
        s.ProbeRawMa[0].Should().BeApproximately(12f, 0.5f, "still water sits mid-range of the nominal 4-20 mA span");

        spy.Mutate = x => x with { ProbeRawMa = [12f, 2.5f, 1f] }; // channel 3 is not active
        time.Advance(TimeSpan.FromSeconds(0.04));
        await svc.TickAsync(Ct);
        alerts.Should().ContainSingle(a => a.Source == AlertSources.WaveProbe).Which.Description.Should().Contain("Probe 2");
        spy.Commands.Should().Equal(PlcControlCommand.SmoothStop);

        time.Advance(TimeSpan.FromSeconds(0.04));
        await svc.TickAsync(Ct);
        alerts.Count(a => a.Source == AlertSources.WaveProbe).Should().Be(1, "latched until the loop recovers");
    }

    /// <summary>Records commands and lets a test tamper with telemetry.</summary>
    private sealed class SpyDriver(IPlcDriver inner) : IPlcDriver
    {
        public List<PlcControlCommand> Commands { get; } = [];
        public Func<TelemetrySnapshot, TelemetrySnapshot> Mutate { get; set; } = s => s;

        public PlcProtocolType Protocol => inner.Protocol;
        public DriverCapabilities Capabilities => inner.Capabilities;
        public ConnectionState State => inner.State;
        public long CommErrorCount => inner.CommErrorCount;
        public string? LastError => inner.LastError;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged { add { } remove { } }
        public Task<ConnectResult> ConnectAsync(PlcEndpoint e, CancellationToken ct) => inner.ConnectAsync(e, ct);
        public Task DisconnectAsync() => inner.DisconnectAsync();
        public Task<PingResult> PingAsync(CancellationToken ct) => inner.PingAsync(ct);
        public Task<bool> SendWaveRecipeAsync(WaveCommandPacket p, CancellationToken ct) => inner.SendWaveRecipeAsync(p, ct);
        public Task<bool> UploadTrajectoryChunkAsync(TrajectoryChunk c, CancellationToken ct) => inner.UploadTrajectoryChunkAsync(c, ct);
        public Task SendHeartbeatAsync(ushort v, CancellationToken ct) => inner.SendHeartbeatAsync(v, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public Task<bool> SendControlCommandAsync(PlcControlCommand cmd, CancellationToken ct)
        {
            Commands.Add(cmd);
            return inner.SendControlCommandAsync(cmd, ct);
        }

        public async Task<TelemetrySnapshot> PollTelemetryAsync(CancellationToken ct) => Mutate(await inner.PollTelemetryAsync(ct));

        public async Task<TelemetryBlock> ReadTelemetryBlockAsync(CancellationToken ct)
        {
            var b = await inner.ReadTelemetryBlockAsync(ct);
            return b with { Samples = b.Samples.Select(Mutate).ToList() };
        }
    }
}
