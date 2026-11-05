using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

/// <summary>EtherNet/IP and S7 (release 2): pure parts tested here; real-PLC tests need hardware.</summary>
public class Release2DriverTests
{
    private static TagEntry Tag(TagDataType t, string address, int length = 1) =>
        new() { Signal = "X", Address = address, DataType = t, Length = length };

    [Theory]
    [InlineData("DB10.DBD4", 10, 'D', 4, 0)]
    [InlineData("db3.dbw120", 3, 'W', 120, 0)]
    [InlineData("DB10.DBX82.5", 10, 'X', 82, 5)]
    [InlineData("DB1.DBB0", 1, 'B', 0, 0)]
    public void S7_address_parser(string text, int db, char kind, int offset, int bit)
    {
        var (a, err) = S7Driver.Parse(text);
        err.Should().BeNull();
        a.Should().Be(new S7Address(db, kind, offset, bit));
    }

    [Theory]
    [InlineData("DB10.DBD")]
    [InlineData("DB10.DBX4")]
    [InlineData("DB10.DBW4.1")]
    [InlineData("M10.0")]
    [InlineData("DB10.DBX4.8")]
    public void S7_bad_addresses_are_rejected(string text) => S7Driver.Parse(text).Error.Should().NotBeNull();

    [Fact]
    public void S7_type_must_match_address_kind()
    {
        var d = new S7Driver(TagMapAndChunkTests.Profile("siemens_s7_db.json"));
        d.CheckAddress(Tag(TagDataType.Float32, "DB10.DBD4")).Should().BeNull();
        d.CheckAddress(Tag(TagDataType.Float32, "DB10.DBW4")).Should().Contain("DBD");
        d.CheckAddress(Tag(TagDataType.Bool, "DB10.DBX4.0", 2)).Should().Contain("arrays");
    }

    [Fact]
    public void S7_big_endian_codec()
    {
        var real = Tag(TagDataType.Float32, "DB10.DBD0", 2);
        S7Driver.Encode(real, [1.0, -2.0]).Should().Equal(0x3F, 0x80, 0, 0, 0xC0, 0, 0, 0);
        S7Driver.Decode(real, default, S7Driver.Encode(real, [1.5, 250.25])).Should().Equal(1.5, 250.25);
        var dint = Tag(TagDataType.Int32, "DB10.DBD0");
        S7Driver.Encode(dint, [-2]).Should().Equal(0xFF, 0xFF, 0xFF, 0xFE);
        S7Driver.Decode(Tag(TagDataType.Bool, "DB10.DBX0.3"), new S7Address(10, 'X', 0, 3), [0b0000_1000]).Should().Equal(1);
    }

    [Fact]
    public void S7_overlap_is_detected()
    {
        var map = TagMapAndChunkTests.Profile("siemens_s7_db.json");
        var bad = map with { Tags = map.Tags.Select(t => t.Signal == "FaultWord" ? t with { Address = "DB10.DBW440" } : t).ToList() };
        new S7Driver(bad).ValidateTagMap().Should().Contain(e => e.Contains("overlap"));
    }

    [Fact]
    public void Eip_tag_name_check()
    {
        var d = new EtherNetIpDriver(TagMapAndChunkTests.Profile("omron_eip_nx.json"));
        d.CheckAddress(Tag(TagDataType.Float32, "Wave.Period")).Should().BeNull();
        d.CheckAddress(Tag(TagDataType.Float32, "Program:Main.Wave[3].Amp")).Should().BeNull();
        d.CheckAddress(Tag(TagDataType.Float32, "Wave.Amp[")).Should().NotBeNull();
        d.CheckAddress(Tag(TagDataType.Float32, "Wave Period")).Should().NotBeNull();
        EtherNetIpDriver.ElementSize(TagDataType.UInt16).Should().Be(2);
        EtherNetIpDriver.Reason("ErrorTimeout").Should().Be(FailureReason.Timeout);
        EtherNetIpDriver.Reason("ErrorNotFound").Should().Be(FailureReason.InvalidConfig);
    }

    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task S7_connect_to_closed_port_fails_cleanly()
    {
        await using var d = new S7Driver(TagMapAndChunkTests.Profile("siemens_s7_db.json"));
        var r = await d.ConnectAsync(new PlcEndpoint { IpAddress = "127.0.0.1", Port = ClosedPort(), TimeoutMs = 500 }, default);
        r.Ok.Should().BeFalse();
        r.Reason.Should().BeOneOf(FailureReason.Refused, FailureReason.Timeout);
    }

    [Fact]
    public async Task Eip_connect_to_closed_port_fails_cleanly_and_native_library_loads()
    {
        await using var d = new EtherNetIpDriver(TagMapAndChunkTests.Profile("omron_eip_nx.json"));
        var r = await d.ConnectAsync(new PlcEndpoint { IpAddress = "127.0.0.1", Port = ClosedPort(), TimeoutMs = 500 }, default);
        r.Ok.Should().BeFalse();
        r.Message.Should().StartWith("EtherNet/IP");
    }
}
