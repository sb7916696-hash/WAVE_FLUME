using System.Buffers.Binary;

namespace WaveFlume.Plc;

/// <summary>
/// Byte order of a 32-bit value across two 16-bit registers, named by where the big-endian
/// bytes A (MSB) B C D land. ABCD = Modbus/FINS standard, CDAB = word swap.
/// </summary>
public enum WordOrder { ABCD, CDAB, BADC, DCBA }

/// <summary>Engineering values ⇄ 16-bit register words for register-based protocols.</summary>
public static class WordCodec
{
    public static int WordsPerValue(TagDataType type) => type is TagDataType.Int32 or TagDataType.Float32 ? 2 : 1;

    public static int WordCount(TagEntry tag) => WordsPerValue(tag.DataType) * tag.Length;

    public static ushort[] Encode(TagEntry tag, IReadOnlyList<double> values, WordOrder order)
    {
        if (values.Count != tag.Length)
            throw new ArgumentException($"{tag.Signal} expects {tag.Length} values, got {values.Count}.", nameof(values));
        int wpv = WordsPerValue(tag.DataType);
        var words = new ushort[tag.Length * wpv];
        Span<byte> b = stackalloc byte[4];
        for (int i = 0; i < values.Count; i++)
        {
            double raw = tag.ToRaw(values[i]);
            switch (tag.DataType)
            {
                case TagDataType.Bool:
                    words[i] = Swap16(raw != 0 ? (ushort)1 : (ushort)0, order);
                    break;
                case TagDataType.UInt16:
                    words[i] = Swap16(checked((ushort)CheckedRound(raw, ushort.MinValue, ushort.MaxValue, tag)), order);
                    break;
                case TagDataType.Int32:
                    BinaryPrimitives.WriteInt32BigEndian(b, (int)CheckedRound(raw, int.MinValue, int.MaxValue, tag));
                    Place(b, words.AsSpan(i * 2, 2), order);
                    break;
                case TagDataType.Float32:
                    BinaryPrimitives.WriteSingleBigEndian(b, (float)raw);
                    Place(b, words.AsSpan(i * 2, 2), order);
                    break;
            }
        }
        return words;
    }

    public static double[] Decode(TagEntry tag, ReadOnlySpan<ushort> words, WordOrder order)
    {
        int wpv = WordsPerValue(tag.DataType);
        if (words.Length != tag.Length * wpv)
            throw new ArgumentException($"{tag.Signal} expects {tag.Length * wpv} words, got {words.Length}.", nameof(words));
        var values = new double[tag.Length];
        Span<byte> b = stackalloc byte[4];
        for (int i = 0; i < tag.Length; i++)
        {
            double raw = tag.DataType switch
            {
                TagDataType.Bool => Swap16(words[i], order) != 0 ? 1 : 0,
                TagDataType.UInt16 => Swap16(words[i], order),
                TagDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(Gather(words.Slice(i * 2, 2), order, b)),
                TagDataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(Gather(words.Slice(i * 2, 2), order, b)),
                _ => throw new ArgumentOutOfRangeException(nameof(tag)),
            };
            values[i] = tag.DataType == TagDataType.Bool ? raw : tag.ToEngineering(raw);
        }
        return values;
    }

    private static double CheckedRound(double raw, double min, double max, TagEntry tag)
    {
        double r = Math.Round(raw);
        if (double.IsNaN(r) || r < min || r > max)
            throw new OverflowException($"{tag.Signal}: raw value {raw} does not fit {tag.DataType}.");
        return r;
    }

    private static bool SwapsBytes(WordOrder o) => o is WordOrder.BADC or WordOrder.DCBA;

    private static ushort Swap16(ushort w, WordOrder o) => SwapsBytes(o) ? BinaryPrimitives.ReverseEndianness(w) : w;

    /// <summary>Big-endian ABCD bytes → two register words in the requested order.</summary>
    private static void Place(ReadOnlySpan<byte> abcd, Span<ushort> dst, WordOrder o)
    {
        ushort ab = (ushort)(abcd[0] << 8 | abcd[1]), cd = (ushort)(abcd[2] << 8 | abcd[3]);
        (ushort hi, ushort lo) = o is WordOrder.ABCD or WordOrder.BADC ? (ab, cd) : (cd, ab);
        dst[0] = Swap16(hi, o);
        dst[1] = Swap16(lo, o);
    }

    private static Span<byte> Gather(ReadOnlySpan<ushort> src, WordOrder o, Span<byte> abcd)
    {
        ushort w0 = Swap16(src[0], o), w1 = Swap16(src[1], o);
        (ushort ab, ushort cd) = o is WordOrder.ABCD or WordOrder.BADC ? (w0, w1) : (w1, w0);
        abcd[0] = (byte)(ab >> 8); abcd[1] = (byte)ab; abcd[2] = (byte)(cd >> 8); abcd[3] = (byte)cd;
        return abcd;
    }
}
