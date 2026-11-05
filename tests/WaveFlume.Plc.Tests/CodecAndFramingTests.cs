using System.Net;
using System.Text;
using FluentAssertions;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

public class CodecAndFramingTests
{
    private static TagEntry Tag(TagDataType type, int length = 1, double scale = 1, double offset = 0) =>
        new() { Signal = "X", Address = "D0", DataType = type, Length = length, Scale = scale, Offset = offset };

    [Theory]
    [InlineData(WordOrder.ABCD, 0x3F80, 0x0000)]
    [InlineData(WordOrder.CDAB, 0x0000, 0x3F80)]
    [InlineData(WordOrder.BADC, 0x803F, 0x0000)]
    [InlineData(WordOrder.DCBA, 0x0000, 0x803F)]
    public void Float_one_lands_in_documented_word_order(WordOrder order, int w0, int w1)
    {
        var words = WordCodec.Encode(Tag(TagDataType.Float32), [1.0], order);
        words.Should().Equal((ushort)w0, (ushort)w1);
        WordCodec.Decode(Tag(TagDataType.Float32), words, order).Should().Equal(1.0);
    }

    [Theory]
    [InlineData(WordOrder.ABCD)]
    [InlineData(WordOrder.CDAB)]
    [InlineData(WordOrder.BADC)]
    [InlineData(WordOrder.DCBA)]
    public void Round_trip_all_types(WordOrder order)
    {
        var cases = new (TagEntry Tag, double[] Values)[]
        {
            (Tag(TagDataType.Int32, 3), [-2, 0, 123_456_789]),
            (Tag(TagDataType.UInt16, 2), [0, 65535]),
            (Tag(TagDataType.Bool), [1]),
            (Tag(TagDataType.Float32, 2), [-350.25, 1e-3]),
        };
        foreach (var (tag, values) in cases)
        {
            var back = WordCodec.Decode(tag, WordCodec.Encode(tag, values, order), order);
            for (int i = 0; i < values.Length; i++) back[i].Should().BeApproximately(values[i], Math.Abs(values[i]) * 1e-7);
        }
        WordCodec.Encode(Tag(TagDataType.Int32), [-2], WordOrder.ABCD).Should().Equal(0xFFFF, 0xFFFE);
    }

    [Fact]
    public void Scale_offset_and_overflow()
    {
        var tag = Tag(TagDataType.UInt16, scale: 0.1, offset: -10); // eng = raw·0.1 − 10
        WordCodec.Encode(tag, [0], WordOrder.ABCD).Should().Equal(100);
        WordCodec.Decode(tag, new ushort[] { 250 }, WordOrder.ABCD).Should().Equal(15);
        FluentActions.Invoking(() => WordCodec.Encode(Tag(TagDataType.UInt16), [70000], WordOrder.ABCD)).Should().Throw<OverflowException>();
        FluentActions.Invoking(() => WordCodec.Encode(Tag(TagDataType.UInt16), [-1], WordOrder.ABCD)).Should().Throw<OverflowException>();
    }

    [Fact]
    public void Crc16_modbus_check_value()
    {
        Crc16.Compute(Encoding.ASCII.GetBytes("123456789")).Should().Be(0x4B37);
        // 1.0f, 2.0f big-endian = 3F 80 00 00 40 00 00 00
        Crc16.OfFloats([1f, 2f]).Should().Be(Crc16.Compute(new byte[] { 0x3F, 0x80, 0, 0, 0x40, 0, 0, 0 }));
    }

    private static readonly FinsAddress Addr = new(0x00, 0x01, 0x00, 0x00, 0x0A, 0x00);

    [Fact]
    public void Fins_read_frame_matches_hand_built_bytes()
    {
        FinsFrame.BuildRead(Addr, 0x07, 100, 12).Should().Equal(
            0x80, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00, 0x0A, 0x00, 0x07, // header
            0x01, 0x01,                                                 // memory area read
            0x82, 0x00, 0x64, 0x00,                                     // DM, word 100, bit 0
            0x00, 0x0C);                                                // 12 words
    }

    [Fact]
    public void Fins_write_frame_matches_hand_built_bytes()
    {
        FinsFrame.BuildWrite(Addr, 0xFF, 0x1234, [0xABCD, 0x0001]).Should().Equal(
            0x80, 0x00, 0x02, 0x00, 0x01, 0x00, 0x00, 0x0A, 0x00, 0xFF,
            0x01, 0x02,
            0x82, 0x12, 0x34, 0x00,
            0x00, 0x02,
            0xAB, 0xCD, 0x00, 0x01);
    }

    [Fact]
    public void Fins_response_parsing()
    {
        byte[] ok = [0xC0, 0, 2, 0, 0x0A, 0, 0, 0x01, 0, 0x07, 0x01, 0x01, 0x00, 0x00, 0x12, 0x34, 0xAB, 0xCD];
        FinsFrame.ParseResponse(ok, 0x07, FinsFrame.MemoryAreaRead).Should().Equal(0x1234, 0xABCD);
        FinsFrame.ParseResponse(ok, 0x08, FinsFrame.MemoryAreaRead).Should().BeNull("stale SID is ignored");

        byte[] flagged = (byte[])ok.Clone();
        flagged[13] = 0x40; // PLC non-fatal flag only — still 0x0000
        FinsFrame.ParseResponse(flagged, 0x07, FinsFrame.MemoryAreaRead).Should().NotBeNull();

        byte[] wrongNode = [0xC0, 0, 2, 0, 0x0A, 0, 0, 0x01, 0, 0x07, 0x01, 0x01, 0x01, 0x01];
        FluentActions.Invoking(() => FinsFrame.ParseResponse(wrongNode, 0x07, FinsFrame.MemoryAreaRead))
            .Should().Throw<PlcProtocolException>().Which.Reason.Should().Be(FailureReason.WrongNode);
    }

    [Fact]
    public void Fins_tcp_envelope()
    {
        FinsFrame.TcpEnvelope(FinsFrame.TcpNodeRequest, new byte[4]).Should().Equal(
            (byte)'F', (byte)'I', (byte)'N', (byte)'S', 0, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    [Fact]
    public void Network_hint()
    {
        NetworkHint.TryParseIPv4("192.168.250.1", out var plc).Should().BeTrue();
        NetworkHint.TryParseIPv4("192.168.250", out _).Should().BeFalse();
        NetworkHint.TryParseIPv4("192.168.250.256", out _).Should().BeFalse();
        var pc = new[] { (IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0")) };
        NetworkHint.Check(plc, pc).Should().Contain("not on any PC subnet").And.Contain("192.168.1.10");
        NetworkHint.Check(plc, [(IPAddress.Parse("192.168.250.5"), IPAddress.Parse("255.255.255.0"))]).Should().BeNull();
    }
}
