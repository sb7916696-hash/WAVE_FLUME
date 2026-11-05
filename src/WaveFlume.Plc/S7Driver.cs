using System.Buffers.Binary;
using System.Text.RegularExpressions;
using S7.Net;
using S7Plc = S7.Net.Plc;

namespace WaveFlume.Plc;

/// <summary>Parsed S7 data-block address: DB10.DBX0.0 (bit), DBB1 (byte), DBW2 (word), DBD4 (dword).</summary>
public readonly record struct S7Address(int Db, char Kind, int ByteOffset, int Bit);

/// <summary>
/// Siemens S7comm via S7netplus (S7-1200/1500 need PUT/GET enabled and the DB not optimised).
/// BOOL → DBX bit, UINT16 → DBW, INT32/FLOAT32 → DBD; arrays are contiguous. Data are big-endian.
/// Extras: S7Rack, S7Slot, S7CpuType (S71500 default).
/// </summary>
public sealed partial class S7Driver(TagMap map) : TagMapDriverBase(map)
{
    /// <summary>Default PDU (240 bytes) minus headers ≈ 220 data bytes per request.</summary>
    public const int MaxWordsPerRequest = 100;

    private S7Plc? _plc;

    public override PlcProtocolType Protocol => PlcProtocolType.S7;
    public override DriverCapabilities Capabilities { get; } = new(MaxWordsPerRequest, true, false);

    [GeneratedRegex(@"^DB(\d+)\.DB([XBWD])(\d+)(?:\.([0-7]))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressPattern();

    public static (S7Address Address, string? Error) Parse(string address)
    {
        var m = AddressPattern().Match(address.Trim());
        if (!m.Success) return (default, $"'{address}' must look like DB10.DBD4, DB10.DBW2 or DB10.DBX0.0.");
        char kind = char.ToUpperInvariant(m.Groups[2].Value[0]);
        bool hasBit = m.Groups[4].Success;
        if (kind == 'X' != hasBit) return (default, $"'{address}': only DBX takes a bit number (DBX0.0).");
        return (new S7Address(int.Parse(m.Groups[1].Value), kind, int.Parse(m.Groups[3].Value), hasBit ? int.Parse(m.Groups[4].Value) : 0), null);
    }

    public static char KindFor(TagDataType t) => t switch
    {
        TagDataType.Bool => 'X',
        TagDataType.UInt16 => 'W',
        _ => 'D',
    };

    public override string? CheckAddress(TagEntry tag)
    {
        var (a, err) = Parse(tag.Address);
        if (err is not null) return err;
        if (a.Kind != KindFor(tag.DataType)) return $"{tag.DataType} needs DB{KindFor(tag.DataType)}, got DB{a.Kind}.";
        if (tag.DataType == TagDataType.Bool && tag.Length != 1) return "BOOL arrays are not supported; use one bit per signal.";
        return null;
    }

    /// <summary>Overlap check across byte ranges within each DB.</summary>
    public override IReadOnlyList<string> ValidateTagMap()
    {
        var errors = base.ValidateTagMap().ToList();
        if (errors.Count > 0) return errors;
        var spans = Map.Tags.Select(t => (t.Signal, A: Parse(t.Address).Address, Bytes: ByteCount(t)))
            .GroupBy(s => s.A.Db);
        foreach (var db in spans)
        {
            var sorted = db.OrderBy(s => s.A.ByteOffset).ThenBy(s => s.A.Bit).ToList();
            for (int i = 1; i < sorted.Count; i++)
            {
                var (prev, cur) = (sorted[i - 1], sorted[i]);
                bool sameBit = prev.A.Kind == 'X' && cur.A.Kind == 'X' && prev.A.ByteOffset == cur.A.ByteOffset;
                if (sameBit ? prev.A.Bit == cur.A.Bit : cur.A.ByteOffset < prev.A.ByteOffset + prev.Bytes)
                    errors.Add($"Signals '{prev.Signal}' and '{cur.Signal}' overlap in DB{db.Key}.");
            }
        }
        return errors;
    }

    public static int ByteCount(TagEntry t) => t.DataType == TagDataType.Bool ? 1 : WordCodec.WordCount(t) * 2;

    protected override async Task ConnectCoreAsync(PlcEndpoint ep, CancellationToken ct)
    {
        var cpu = Enum.TryParse<CpuType>(ep.Extra("S7CpuType"), true, out var c) ? c : CpuType.S71500;
        var plc = new S7Plc(cpu, ep.IpAddress, ep.Port, (short)ep.ExtraInt("S7Rack", 0), (short)ep.ExtraInt("S7Slot", 1))
        {
            ReadTimeout = ep.TimeoutMs,
            WriteTimeout = ep.TimeoutMs,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeoutMs(ep));
        await Call(() => plc.OpenAsync(timeout.Token)).ConfigureAwait(false);
        _plc = plc;
    }

    protected override Task DisconnectCoreAsync()
    {
        _plc?.Close();
        _plc = null;
        return Task.CompletedTask;
    }

    protected override async Task<double[]> ReadTagCoreAsync(TagEntry tag, CancellationToken ct)
    {
        var a = Parse(tag.Address).Address;
        var bytes = await Call(() => Device.ReadBytesAsync(DataType.DataBlock, a.Db, a.ByteOffset, ByteCount(tag), ct)).ConfigureAwait(false);
        return Decode(tag, a, bytes);
    }

    protected override async Task WriteTagCoreAsync(TagEntry tag, double[] values, CancellationToken ct)
    {
        var a = Parse(tag.Address).Address;
        if (tag.DataType == TagDataType.Bool)
            await Call(() => Device.WriteBitAsync(DataType.DataBlock, a.Db, a.ByteOffset, a.Bit, values[0] != 0, ct)).ConfigureAwait(false);
        else
            await Call(() => Device.WriteBytesAsync(DataType.DataBlock, a.Db, a.ByteOffset, Encode(tag, values), ct)).ConfigureAwait(false);
    }

    /// <summary>S7 is big-endian: bytes → ABCD words → <see cref="WordCodec"/>.</summary>
    public static double[] Decode(TagEntry tag, S7Address a, byte[] bytes)
    {
        if (tag.DataType == TagDataType.Bool) return [(bytes[0] >> a.Bit) & 1];
        var words = new ushort[bytes.Length / 2];
        for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i * 2));
        return WordCodec.Decode(tag, words, WordOrder.ABCD);
    }

    public static byte[] Encode(TagEntry tag, double[] values)
    {
        var words = WordCodec.Encode(tag, values, WordOrder.ABCD);
        var bytes = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), words[i]);
        return bytes;
    }

    private S7Plc Device => _plc ?? throw new PlcProtocolException(FailureReason.ProtocolError, "Not connected.");

    private static async Task<T> Call<T>(Func<Task<T>> op)
    {
        try { return await op().ConfigureAwait(false); }
        catch (PlcException ex) { throw Translate(ex); }
    }

    private static Task Call(Func<Task> op) => Call(async () => { await op().ConfigureAwait(false); return 0; });

    private static PlcProtocolException Translate(PlcException ex) => new(ex.ErrorCode switch
    {
        ErrorCode.ConnectionError when ex.InnerException is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused }
            => FailureReason.Refused,
        ErrorCode.ConnectionError => FailureReason.Timeout,
        ErrorCode.WrongCPU_Type => FailureReason.WrongNode,
        _ when ex.Message.Contains("access", StringComparison.OrdinalIgnoreCase) => FailureReason.AccessDenied,
        _ => FailureReason.ProtocolError,
    }, $"S7: {ex.Message}{(ex.InnerException is { } i ? $" ({i.Message})" : "")}");
}
