using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NModbus;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

/// <summary>Real drivers against in-process fake servers over loopback.</summary>
public class DriverIntegrationTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static WaveCommandPacket Recipe => new(1.25, 2.0, 20, [10, 11, 12, 13, 14, 15], [0, -30, -60, -90, -120, -150]);

    private static PlcEndpoint FinsEndpoint(int port, PlcTransport transport) =>
        new() { IpAddress = "127.0.0.1", Port = port, Transport = transport, TimeoutMs = 200, Retries = 3 };

    [Theory]
    [InlineData(PlcTransport.Udp)]
    [InlineData(PlcTransport.Tcp)]
    public async Task Fins_recipe_telemetry_and_trajectory(PlcTransport transport)
    {
        var map = TagMapAndChunkTests.Profile("omron_fins_dm.json");
        using var plc = new FakeFinsPlc(map);
        plc.Set(Signals.StatusWord, (ushort)(StatusBits.Enabled | StatusBits.Homed));
        plc.Set(Signals.PosMm, 1, 2, 3, 4, 5, -6.5);
        plc.Set(Signals.ProbeM, 0.01, 0.02, 0, 0, 0, 0, 0, -0.03);
        plc.Set(Signals.SampleCounter, 123_456);
        plc.Set(Signals.Heartbeat, 7);

        await using var driver = new OmronFinsDriver(map);
        var port = transport == PlcTransport.Udp ? plc.UdpPort : plc.TcpPort;
        var conn = await driver.ConnectAsync(FinsEndpoint(port, transport), Ct);
        conn.Ok.Should().BeTrue(conn.Message);

        (await driver.PingAsync(Ct)).TestTagValue.Should().Be((double)(StatusBits.Enabled | StatusBits.Homed));

        (await driver.SendWaveRecipeAsync(Recipe, Ct)).Should().BeTrue();
        plc.Get(Signals.PeriodS).Should().Equal(1.25);
        plc.Get(Signals.Cycles).Should().Equal(20);
        plc.Get(Signals.AmpMm).Should().Equal(10, 11, 12, 13, 14, 15);
        plc.Get(Signals.PhaseDeg)[5].Should().Be(-150);

        (await driver.SendControlCommandAsync(PlcControlCommand.Enable, Ct)).Should().BeTrue();
        (await driver.SendControlCommandAsync(PlcControlCommand.Start, Ct)).Should().BeTrue();
        ((CommandBits)plc.Get(Signals.CmdWord)[0]).Should().Be(CommandBits.Enable | CommandBits.Start);
        (await driver.SendControlCommandAsync(PlcControlCommand.SmoothStop, Ct)).Should().BeTrue();
        ((CommandBits)plc.Get(Signals.CmdWord)[0]).Should().Be(CommandBits.Enable | CommandBits.SmoothStop, "pulse bits clear on the next command");

        await driver.SendHeartbeatAsync(1, Ct);
        plc.Get(Signals.HeartbeatPc).Should().Equal(1);

        var t = await driver.PollTelemetryAsync(Ct);
        t.PaddleMm.Should().Equal(1, 2, 3, 4, 5, -6.5f);
        t.ProbeM[7].Should().BeApproximately(-0.03f, 1e-6f);
        t.SampleCounter.Should().Be(123_456);
        t.Heartbeat.Should().Be(7);

        var traj = TagMapAndChunkTests.Ramp(25);
        int perChunk = TrajectoryChunker.ValuesPerChunk(driver.Capabilities.MaxBlockWords, 2, map.Get(Signals.TrajChunk).Length, 6);
        (await TrajectoryChunker.UploadAsync(driver, traj, perChunk, null, Ct)).Should().BeTrue(driver.LastError);
        plc.Get(Signals.Cycles).Should().Equal(25);
        ((CommandBits)plc.Get(Signals.CmdWord)[0]).Should().HaveFlag(CommandBits.ModeTrajectory);
        plc.ReceivedTrajectoryMm.Take(25 * 6).Should().Equal(TrajectoryChunker.Split(traj, 25 * 6).Single().ValuesMm);
    }

    [Fact]
    public async Task Fins_retries_dropped_frames_then_faults()
    {
        var map = TagMapAndChunkTests.Profile("omron_fins_dm.json");
        using var plc = new FakeFinsPlc(map);
        await using var driver = new OmronFinsDriver(map);
        (await driver.ConnectAsync(FinsEndpoint(plc.UdpPort, PlcTransport.Udp), Ct)).Ok.Should().BeTrue();

        plc.DropResponses = 2;
        (await driver.PingAsync(Ct)).Ok.Should().BeTrue("two drops are within 3 retries");

        plc.DropResponses = 10;
        var ping = await driver.PingAsync(Ct);
        ping.Ok.Should().BeFalse();
        ping.Reason.Should().Be(FailureReason.Timeout);
        driver.CommErrorCount.Should().BeGreaterThan(0);
        driver.State.Should().Be(ConnectionState.Fault);
    }

    [Fact]
    public async Task Fins_wrong_node_is_reported()
    {
        var map = TagMapAndChunkTests.Profile("omron_fins_dm.json");
        using var plc = new FakeFinsPlc(map) { ForcedEndCode = 0x0101 };
        await using var driver = new OmronFinsDriver(map);
        var r = await driver.ConnectAsync(FinsEndpoint(plc.UdpPort, PlcTransport.Udp), Ct);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Be(FailureReason.WrongNode);
    }

    [Fact]
    public async Task Fins_chunk_with_bad_crc_is_not_acknowledged()
    {
        var map = TagMapAndChunkTests.Profile("omron_fins_dm.json");
        using var plc = new FakeFinsPlc(map);
        await using var driver = new OmronFinsDriver(map);
        await driver.ConnectAsync(FinsEndpoint(plc.UdpPort, PlcTransport.Udp), Ct);
        var chunk = TrajectoryChunker.Split(TagMapAndChunkTests.Ramp(10), 60).Single();
        (await driver.UploadTrajectoryChunkAsync(chunk with { Crc = (ushort)~chunk.Crc }, Ct)).Should().BeFalse();
        driver.LastError.Should().Contain("not acknowledged");
    }

    [Fact]
    public async Task Modbus_against_nmodbus_slave()
    {
        var map = TagMapAndChunkTests.Profile("modbus_generic.json");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var factory = new ModbusFactory();
        using var network = factory.CreateSlaveNetwork(listener);
        var slave = factory.CreateSlave(1);
        network.AddSlave(slave);
        using var cts = new CancellationTokenSource();
        _ = network.ListenAsync(cts.Token);

        var pos = map.Get(Signals.PosMm);
        slave.DataStore.HoldingRegisters.WritePoints(100, WordCodec.Encode(pos, [1, 2, 3, 4, 5, 6], map.WordOrder));

        await using var driver = new ModbusTcpDriver(map);
        var ep = new PlcEndpoint { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, TimeoutMs = 1000 };
        var conn = await driver.ConnectAsync(ep, Ct);
        conn.Ok.Should().BeTrue(conn.Message);

        (await driver.SendWaveRecipeAsync(Recipe, Ct)).Should().BeTrue(driver.LastError);
        var period = slave.DataStore.HoldingRegisters.ReadPoints(2, 2);
        WordCodec.Decode(map.Get(Signals.PeriodS), period, map.WordOrder).Should().Equal(1.25);

        (await driver.PollTelemetryAsync(Ct)).PaddleMm.Should().Equal(1, 2, 3, 4, 5, 6);

        // Writes above 123 registers are split into several FC16 requests.
        var big = map with { Tags = map.Tags.Select(t => t.Signal == Signals.TrajChunk ? t with { Length = 120 } : t).ToList() };
        await using var bigDriver = new ModbusTcpDriver(big);
        (await bigDriver.ConnectAsync(ep, Ct)).Ok.Should().BeTrue();
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Refused_connection_is_classified()
    {
        var map = TagMapAndChunkTests.Profile("modbus_generic.json");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop(); // nothing listens here now

        await using var driver = new ModbusTcpDriver(map);
        var r = await driver.ConnectAsync(new PlcEndpoint { IpAddress = "127.0.0.1", Port = port, TimeoutMs = 1000 }, Ct);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Be(FailureReason.Refused);
    }
}
