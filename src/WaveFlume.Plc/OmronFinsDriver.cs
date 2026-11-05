using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WaveFlume.Plc;

/// <summary>FINS node addressing: network / node / unit for destination and source.</summary>
public readonly record struct FinsAddress(byte Dna, byte Da1, byte Da2, byte Sna, byte Sa1, byte Sa2);

/// <summary>FINS command frames (spec §9.3). Pure functions — unit-tested against hand-built bytes.</summary>
public static class FinsFrame
{
    public const ushort MemoryAreaRead = 0x0101;
    public const ushort MemoryAreaWrite = 0x0102;
    public const byte AreaDmWord = 0x82;
    public const int HeaderLength = 10;

    public static byte[] BuildRead(FinsAddress a, byte sid, int address, int count) =>
        Build(a, sid, MemoryAreaRead, address, count, ReadOnlySpan<ushort>.Empty);

    public static byte[] BuildWrite(FinsAddress a, byte sid, int address, ReadOnlySpan<ushort> words) =>
        Build(a, sid, MemoryAreaWrite, address, words.Length, words);

    private static byte[] Build(FinsAddress a, byte sid, ushort command, int address, int count, ReadOnlySpan<ushort> data)
    {
        var f = new byte[HeaderLength + 8 + data.Length * 2];
        f[0] = 0x80; f[1] = 0x00; f[2] = 0x02;            // ICF, RSV, GCT
        f[3] = a.Dna; f[4] = a.Da1; f[5] = a.Da2;
        f[6] = a.Sna; f[7] = a.Sa1; f[8] = a.Sa2;
        f[9] = sid;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(10), command);
        f[12] = AreaDmWord;
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(13), (ushort)address);
        f[15] = 0x00;                                       // bit position
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(16), (ushort)count);
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(18 + i * 2), data[i]);
        return f;
    }

    /// <summary>Validates a response and returns its data words. Null when the SID does not match (stale reply).</summary>
    public static ushort[]? ParseResponse(ReadOnlySpan<byte> r, byte sid, ushort command)
    {
        if (r.Length < HeaderLength + 4 || (r[0] & 0x40) == 0)
            throw new PlcProtocolException(FailureReason.ProtocolError, "Malformed FINS response.");
        if (r[9] != sid) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(r[10..]) != command)
            throw new PlcProtocolException(FailureReason.ProtocolError, "FINS response command mismatch.");
        // Strip the relay (0x80 in MRES) and PLC status flags (0xC0 in SRES) before testing for 0x0000.
        ushort end = (ushort)((r[12] & 0x7F) << 8 | (r[13] & 0x3F));
        if (end != 0)
            throw new PlcProtocolException(EndCodeReason(end), $"FINS end code 0x{end:X4}.");
        var data = r[14..];
        var words = new ushort[data.Length / 2];
        for (int i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]);
        return words;
    }

    /// <summary>0x01xx/0x02xx: local/destination node errors; 0x21xx: area protected or read-only.</summary>
    public static FailureReason EndCodeReason(ushort end) => (end >> 8) switch
    {
        0x01 or 0x02 => FailureReason.WrongNode,
        0x21 => FailureReason.AccessDenied,
        _ => FailureReason.ProtocolError,
    };

    // FINS/TCP envelope: "FINS", length (bytes after this field), command, error code.
    public const uint TcpNodeRequest = 0, TcpNodeResponse = 1, TcpFrame = 2;

    public static byte[] TcpEnvelope(uint command, ReadOnlySpan<byte> payload)
    {
        var b = new byte[16 + payload.Length];
        "FINS"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)(8 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), command);
        payload.CopyTo(b.AsSpan(16));
        return b;
    }
}

/// <summary>
/// Omron FINS over UDP (default) or TCP. Addresses are DM words "D&lt;n&gt;" (NX/NJ: variables mapped to fixed DM).
/// Timeout = endpoint TimeoutMs (spec: 100 ms), <see cref="PlcEndpoint.Retries"/> retries, then fault.
/// </summary>
public sealed class OmronFinsDriver(TagMap map) : WordDriverBase(map)
{
    /// <summary>UDP frame limit is 999 read / 996 write words; keep margin.</summary>
    public const int MaxWordsPerCommand = 990;

    private UdpClient? _udp;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private FinsAddress _addr;
    private byte _sid;

    public override PlcProtocolType Protocol => PlcProtocolType.OmronFins;
    public override DriverCapabilities Capabilities { get; } = new(MaxWordsPerCommand, true, false);
    protected override int AddressSpaceWords => 32768;

    protected override (int Start, string? Error) ParseAddress(string address) => ParsePrefixed(address, "D");

    protected override async Task ConnectCoreAsync(PlcEndpoint ep, CancellationToken ct)
    {
        byte plcNode = IPAddress.Parse(ep.IpAddress).GetAddressBytes()[3];
        byte dna = (byte)ep.ExtraInt(EndpointExtras.FinsDestNetwork, 0);
        byte da1 = (byte)ep.ExtraInt(EndpointExtras.FinsDestNode, plcNode);
        byte da2 = (byte)ep.ExtraInt(EndpointExtras.FinsDestUnit, 0);

        if (ep.Transport == PlcTransport.Udp)
        {
            _udp = new UdpClient();
            _udp.Connect(ep.IpAddress, ep.Port);
            byte pcNode = ((IPEndPoint)_udp.Client.LocalEndPoint!).Address.GetAddressBytes()[3];
            _addr = new FinsAddress(dna, da1, da2, 0, (byte)ep.ExtraInt(EndpointExtras.FinsSourceNode, pcNode), 0);
            return;
        }

        _tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeoutMs(ep));
        await _tcp.ConnectAsync(ep.IpAddress, ep.Port, timeout.Token).ConfigureAwait(false);
        timeout.CancelAfter(ep.TimeoutMs);
        _stream = _tcp.GetStream();

        // Node-address handshake; client node 0 = let the PLC assign one.
        var req = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(req, (uint)ep.ExtraInt(EndpointExtras.FinsSourceNode, 0));
        await _stream.WriteAsync(FinsFrame.TcpEnvelope(FinsFrame.TcpNodeRequest, req), timeout.Token).ConfigureAwait(false);
        var (cmd, payload) = await ReadTcpEnvelopeAsync(timeout.Token).ConfigureAwait(false);
        if (cmd != FinsFrame.TcpNodeResponse || payload.Length < 8)
            throw new PlcProtocolException(FailureReason.ProtocolError, "FINS/TCP node handshake failed.");
        byte client = (byte)BinaryPrimitives.ReadUInt32BigEndian(payload);
        byte server = (byte)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4));
        _addr = new FinsAddress(dna, ep.Extras.ContainsKey(EndpointExtras.FinsDestNode) ? da1 : server, da2, 0, client, 0);
    }

    protected override Task DisconnectCoreAsync()
    {
        _udp?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
        (_udp, _stream, _tcp) = (null, null, null);
        return Task.CompletedTask;
    }

    protected override async Task<ushort[]> ReadWordsAsync(int start, int count, CancellationToken ct)
    {
        var words = await TransactAsync(sid => FinsFrame.BuildRead(_addr, sid, start, count), FinsFrame.MemoryAreaRead, ct).ConfigureAwait(false);
        if (words.Length != count)
            throw new PlcProtocolException(FailureReason.ProtocolError, $"FINS returned {words.Length} words, expected {count}.");
        return words;
    }

    protected override Task WriteWordsAsync(int start, ushort[] words, CancellationToken ct) =>
        TransactAsync(sid => FinsFrame.BuildWrite(_addr, sid, start, words), FinsFrame.MemoryAreaWrite, ct);

    private async Task<ushort[]> TransactAsync(Func<byte, byte[]> build, ushort command, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            byte sid = unchecked(++_sid);
            var frame = build(sid);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Endpoint.TimeoutMs);
            try
            {
                await SendAsync(frame, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var words = FinsFrame.ParseResponse(await ReceiveAsync(timeout.Token).ConfigureAwait(false), sid, command);
                    if (words is not null) return words; // otherwise a stale reply: keep listening
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= Endpoint.Retries)
                    throw new PlcProtocolException(FailureReason.Timeout, $"FINS timeout after {attempt + 1} attempts.");
            }
        }
    }

    private async Task SendAsync(byte[] frame, CancellationToken ct)
    {
        if (_udp is not null) await _udp.SendAsync(frame, ct).ConfigureAwait(false);
        else if (_stream is not null) await _stream.WriteAsync(FinsFrame.TcpEnvelope(FinsFrame.TcpFrame, frame), ct).ConfigureAwait(false);
        else throw new PlcProtocolException(FailureReason.ProtocolError, "Not connected.");
    }

    private async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        if (_udp is not null) return (await _udp.ReceiveAsync(ct).ConfigureAwait(false)).Buffer;
        var (cmd, payload) = await ReadTcpEnvelopeAsync(ct).ConfigureAwait(false);
        if (cmd != FinsFrame.TcpFrame)
            throw new PlcProtocolException(FailureReason.ProtocolError, $"Unexpected FINS/TCP command {cmd}.");
        return payload;
    }

    private async Task<(uint Command, byte[] Payload)> ReadTcpEnvelopeAsync(CancellationToken ct)
    {
        var head = new byte[16];
        await _stream!.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (!head.AsSpan(0, 4).SequenceEqual("FINS"u8))
            throw new PlcProtocolException(FailureReason.ProtocolError, "Bad FINS/TCP header.");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        uint command = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
        uint error = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12));
        if (error != 0)
            throw new PlcProtocolException(FailureReason.WrongNode, $"FINS/TCP error code {error} (node address in use or not allowed?).");
        var payload = new byte[length - 8];
        await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return (command, payload);
    }
}
