using System.Net.Sockets;
using NModbus;

namespace WaveFlume.Plc;

/// <summary>
/// Modbus TCP via NModbus. Addresses are holding registers "HR&lt;n&gt;" with n the 0-based PDU address.
/// BOOL occupies a whole register (0/1). Unit ID from <see cref="EndpointExtras.ModbusUnitId"/>.
/// </summary>
public sealed class ModbusTcpDriver(TagMap map) : WordDriverBase(map)
{
    /// <summary>FC16 write limit; FC3 read allows 125.</summary>
    public const int MaxRegistersPerWrite = 123;

    private TcpClient? _tcp;
    private IModbusMaster? _master;
    private byte _unit;

    public override PlcProtocolType Protocol => PlcProtocolType.ModbusTcp;
    public override DriverCapabilities Capabilities { get; } = new(MaxRegistersPerWrite, true, false);
    protected override int AddressSpaceWords => 65536;

    protected override (int Start, string? Error) ParseAddress(string address) => ParsePrefixed(address, "HR");

    protected override async Task ConnectCoreAsync(PlcEndpoint ep, CancellationToken ct)
    {
        _unit = (byte)ep.ExtraInt(EndpointExtras.ModbusUnitId, 1);
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = ep.TimeoutMs, SendTimeout = ep.TimeoutMs };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeoutMs(ep));
        try
        {
            await tcp.ConnectAsync(ep.IpAddress, ep.Port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        _tcp = tcp;
        _master = new ModbusFactory().CreateMaster(tcp);
        _master.Transport.ReadTimeout = ep.TimeoutMs;
        _master.Transport.WriteTimeout = ep.TimeoutMs;
        _master.Transport.Retries = ep.Retries;
    }

    protected override Task DisconnectCoreAsync()
    {
        _master?.Dispose();
        _tcp?.Dispose();
        _master = null;
        _tcp = null;
        return Task.CompletedTask;
    }

    protected override async Task<ushort[]> ReadWordsAsync(int start, int count, CancellationToken ct) =>
        await Call(() => Master.ReadHoldingRegistersAsync(_unit, (ushort)start, (ushort)count)).ConfigureAwait(false);

    protected override Task WriteWordsAsync(int start, ushort[] words, CancellationToken ct) =>
        Call(async () => { await Master.WriteMultipleRegistersAsync(_unit, (ushort)start, words).ConfigureAwait(false); return 0; });

    private IModbusMaster Master => _master ?? throw new PlcProtocolException(FailureReason.ProtocolError, "Not connected.");

    private static async Task<T> Call<T>(Func<Task<T>> op)
    {
        try { return await op().ConfigureAwait(false); }
        catch (SlaveException ex)
        {
            // 0x0A/0x0B = gateway path unavailable / target failed to respond → wrong unit ID.
            throw ex.SlaveExceptionCode is 0x0A or 0x0B
                ? new PlcProtocolException(FailureReason.WrongNode, $"Modbus gateway: unit not reachable (exception {ex.SlaveExceptionCode}).")
                : new PlcProtocolException(FailureReason.ProtocolError, $"Modbus exception {ex.SlaveExceptionCode} (illegal function/address?).");
        }
    }
}
