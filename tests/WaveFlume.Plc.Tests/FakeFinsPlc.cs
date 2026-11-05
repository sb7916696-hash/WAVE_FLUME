using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

/// <summary>
/// In-process FINS PLC (UDP and TCP) with a DM memory and the trajectory chunk handshake:
/// on ChunkReady = 1 it checks sequence and CRC like the real PLC must, then acknowledges.
/// </summary>
public sealed class FakeFinsPlc : IDisposable
{
    public const byte ServerNode = 1, ClientNode = 10;

    private readonly TagMap _map;
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private ushort _lastSeq;

    public ushort[] Dm { get; } = new ushort[32768];
    public List<float> ReceivedTrajectoryMm { get; } = [];
    public int DropResponses { get; set; }
    public ushort ForcedEndCode { get; set; }
    public int UdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int TcpPort => ((IPEndPoint)_tcp.LocalEndpoint).Port;

    public FakeFinsPlc(TagMap map)
    {
        _map = map;
        Set(Signals.BufferLevel, 100_000);
        _tcp.Start();
        _ = UdpLoop();
        _ = TcpLoop();
    }

    public void Set(string signal, params double[] values)
    {
        var tag = _map.Get(signal);
        WordCodec.Encode(tag, values, _map.WordOrder).CopyTo(Dm, Start(tag));
    }

    public double[] Get(string signal)
    {
        var tag = _map.Get(signal);
        return WordCodec.Decode(tag, Dm.AsSpan(Start(tag), WordCodec.WordCount(tag)), _map.WordOrder);
    }

    private static int Start(TagEntry t) => int.Parse(t.Address[1..]);

    private async Task UdpLoop()
    {
        try
        {
            while (true)
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                var resp = Handle(r.Buffer);
                if (resp is not null) await _udp.SendAsync(resp, r.RemoteEndPoint, _cts.Token);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private async Task TcpLoop()
    {
        try
        {
            while (true)
            {
                var client = await _tcp.AcceptTcpClientAsync(_cts.Token);
                _ = Serve(client);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var s = client.GetStream();
        var head = new byte[16];
        try
        {
            while (true)
            {
                await s.ReadExactlyAsync(head, _cts.Token);
                uint len = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
                uint cmd = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
                var payload = new byte[len - 8];
                await s.ReadExactlyAsync(payload, _cts.Token);
                if (cmd == FinsFrame.TcpNodeRequest)
                {
                    var nodes = new byte[8];
                    BinaryPrimitives.WriteUInt32BigEndian(nodes, ClientNode);
                    BinaryPrimitives.WriteUInt32BigEndian(nodes.AsSpan(4), ServerNode);
                    await s.WriteAsync(FinsFrame.TcpEnvelope(FinsFrame.TcpNodeResponse, nodes), _cts.Token);
                }
                else if (Handle(payload) is { } resp)
                    await s.WriteAsync(FinsFrame.TcpEnvelope(FinsFrame.TcpFrame, resp), _cts.Token);
            }
        }
        catch (Exception) when (_cts.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
    }

    private byte[]? Handle(byte[] f)
    {
        lock (_lock)
        {
            if (DropResponses > 0) { DropResponses--; return null; }
            ushort cmd = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(10));
            int addr = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(13));
            int count = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(16));
            var data = new List<byte>();
            if (ForcedEndCode == 0 && cmd == FinsFrame.MemoryAreaRead)
            {
                for (int i = 0; i < count; i++) { data.Add((byte)(Dm[addr + i] >> 8)); data.Add((byte)Dm[addr + i]); }
            }
            else if (ForcedEndCode == 0 && cmd == FinsFrame.MemoryAreaWrite)
            {
                for (int i = 0; i < count; i++) Dm[addr + i] = BinaryPrimitives.ReadUInt16BigEndian(f.AsSpan(18 + i * 2));
                OnWrite(addr, count);
            }
            // Response: swap destination and source, ICF = 0xC0.
            byte[] head = [0xC0, 0, 2, f[6], f[7], f[8], f[3], f[4], f[5], f[9], f[10], f[11], (byte)(ForcedEndCode >> 8), (byte)ForcedEndCode];
            return [.. head, .. data];
        }
    }

    private void OnWrite(int addr, int count)
    {
        int ready = Start(_map.Get(Signals.ChunkReady));
        if (ready < addr || ready >= addr + count || Dm[ready] != 1) return;
        Dm[ready] = 0;
        ushort seq = (ushort)Get(Signals.ChunkSeq)[0];
        var values = Array.ConvertAll(Get(Signals.TrajChunk), v => (float)v);
        if (seq == 1) { ReceivedTrajectoryMm.Clear(); _lastSeq = 0; }
        if (seq != _lastSeq + 1 || Crc16.OfFloats(values) != (ushort)Get(Signals.ChunkCrc)[0]) return; // no ack
        _lastSeq = seq;
        ReceivedTrajectoryMm.AddRange(values);
        Set(Signals.ChunkAck, seq);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _tcp.Stop();
    }
}
