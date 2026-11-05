using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

public class TagMapAndChunkTests
{
    public static TagMap Profile(string file) => TagMap.Load(Path.Combine(AppContext.BaseDirectory, "plc_profiles", file));

    [Theory]
    [InlineData("omron_fins_dm.json", PlcProtocolType.OmronFins)]
    [InlineData("modbus_generic.json", PlcProtocolType.ModbusTcp)]
    [InlineData("opcua_generic.json", PlcProtocolType.OpcUa)]
    [InlineData("omron_eip_nx.json", PlcProtocolType.EtherNetIp)]
    [InlineData("siemens_s7_db.json", PlcProtocolType.S7)]
    public void Shipped_profiles_validate_for_their_driver(string file, PlcProtocolType protocol)
    {
        var map = Profile(file);
        map.Protocol.Should().Be(protocol);
        var driver = (TagMapDriverBase)DriverFactory.Create(protocol, map);
        driver.ValidateTagMap().Should().BeEmpty();
        map.PaddleCount.Should().Be(6);
    }

    [Fact]
    public void Validation_catches_missing_overlap_bad_address_and_access()
    {
        var map = Profile("omron_fins_dm.json");
        var fins = (Func<TagMap, IReadOnlyList<string>>)(m => new OmronFinsDriver(m).ValidateTagMap());

        fins(map with { Tags = map.Tags.Where(t => t.Signal != Signals.Heartbeat).ToList() })
            .Should().Contain(e => e.Contains("'Heartbeat' is not mapped"));
        fins(map with { Tags = map.Tags.Select(t => t.Signal == Signals.FaultWord ? t with { Address = "D220" } : t).ToList() })
            .Should().Contain(e => e.Contains("overlap"));
        fins(map with { Tags = map.Tags.Select(t => t.Signal == Signals.CmdWord ? t with { Address = "HR0" } : t).ToList() })
            .Should().Contain(e => e.Contains("CmdWord"));
        fins(map with { Tags = map.Tags.Select(t => t.Signal == Signals.CmdWord ? t with { Access = TagAccess.R } : t).ToList() })
            .Should().Contain(e => e.Contains("needs access W"));
        fins(map with { Tags = map.Tags.Select(t => t.Signal == Signals.TrajChunk ? t with { Length = 61 } : t).ToList() })
            .Should().Contain(e => e.Contains("multiple of the paddle count"));
    }

    [Fact]
    public async Task Connect_is_refused_while_tagmap_invalid()
    {
        var map = Profile("modbus_generic.json");
        await using var driver = new ModbusTcpDriver(map with { Tags = [] });
        var r = await driver.ConnectAsync(new PlcEndpoint { Port = 1 }, CancellationToken.None);
        r.Ok.Should().BeFalse();
        r.Reason.Should().Be(FailureReason.InvalidConfig);
    }

    [Fact]
    public void Tagmap_json_round_trip()
    {
        var map = Profile("opcua_generic.json");
        var path = Path.GetTempFileName();
        map.Save(path);
        TagMap.Load(path).Tags.Should().BeEquivalentTo(map.Tags);
        File.Delete(path);
    }

    [Fact]
    public void Factory_defers_later_release_protocols()
    {
        FluentActions.Invoking(() => DriverFactory.Create(PlcProtocolType.Ads, null)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => DriverFactory.Create(PlcProtocolType.Slmp, null)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => DriverFactory.Create(PlcProtocolType.S7, null)).Should().Throw<ArgumentNullException>("needs a TagMap");
        DriverFactory.Create(PlcProtocolType.Simulator, null).Should().BeOfType<SimulatorDriver>();
        DriverFactory.DefaultPort(PlcProtocolType.OmronFins).Should().Be(9600);
    }

    public static Trajectory Ramp(int samples, int paddles = 6) =>
        new(Enumerable.Range(0, paddles).Select(p => Enumerable.Range(0, samples).Select(i => (float)(0.001 * i + 0.0001 * p)).ToArray()).ToArray(), 125);

    [Fact]
    public void Chunk_size_respects_max_block_words_and_tag()
    {
        TrajectoryChunker.ValuesPerChunk(123, 2, 60, 6).Should().Be(60);      // Modbus: 120 words
        TrajectoryChunker.ValuesPerChunk(990, 2, 600, 6).Should().Be(492);    // FINS: limited by 990 words
        TrajectoryChunker.ValuesPerChunk(4096, 2, 60, 6).Should().Be(60);     // limited by tag
        FluentActions.Invoking(() => TrajectoryChunker.ValuesPerChunk(10, 2, 60, 6)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Split_sequences_pads_and_checksums()
    {
        var chunks = TrajectoryChunker.Split(Ramp(25), 60).ToList(); // 10 samples per chunk → 3 chunks
        chunks.Select(c => (int)c.Seq).Should().Equal(1, 2, 3);
        chunks.Should().OnlyContain(c => c.ValuesMm.Length == 60 && c.TotalSamples == 25 && c.Crc == Crc16.OfFloats(c.ValuesMm));
        chunks[0].ValuesMm[0..6].Should().Equal(new[] { 0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }, (a, e) => Math.Abs(a - e) < 1e-5f); // mm, sample-major
        chunks[1].ValuesMm[0].Should().BeApproximately(10f, 1e-4f);                        // sample 10, paddle 1
        chunks[2].ValuesMm[^6..].Should().Equal(chunks[2].ValuesMm[24..30], "padding repeats sample 24");
    }

    [Fact]
    public async Task Upload_reports_progress_and_stops_on_rejection()
    {
        await using var sim = new SimulatorDriver(new SimulatorOptions());
        await sim.ConnectAsync(new PlcEndpoint(), CancellationToken.None);
        var progress = new List<double>();
        (await TrajectoryChunker.UploadAsync(sim, Ramp(25), 60, new SyncProgress(progress), CancellationToken.None)).Should().BeTrue();
        progress.Should().Equal(1 / 3.0, 2 / 3.0, 1.0);

        var bad = TrajectoryChunker.Split(Ramp(25), 60).ToList();
        (await sim.UploadTrajectoryChunkAsync(bad[0], default)).Should().BeTrue();
        (await sim.UploadTrajectoryChunkAsync(bad[2], default)).Should().BeFalse("sequence gap");
        sim.LastError.Should().Contain("sequence");
        (await sim.UploadTrajectoryChunkAsync(bad[1] with { Crc = (ushort)(bad[1].Crc ^ 1) }, default)).Should().BeFalse("CRC");
        sim.LastError.Should().Contain("CRC");
    }

    public sealed class SyncProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
