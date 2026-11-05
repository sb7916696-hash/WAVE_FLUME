using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WaveFlume.Plc;

public static class DriverFactory
{
    public static IPlcDriver Create(PlcProtocolType protocol, TagMap? map, SimulatorOptions? simulator = null, TimeProvider? time = null)
    {
        TagMap Map() => map ?? throw new ArgumentNullException(nameof(map), $"{protocol} needs a TagMap profile.");
        return protocol switch
        {
            PlcProtocolType.Simulator => new SimulatorDriver(simulator ?? new SimulatorOptions(), time),
            PlcProtocolType.ModbusTcp => new ModbusTcpDriver(Map()),
            PlcProtocolType.OmronFins => new OmronFinsDriver(Map()),
            PlcProtocolType.OpcUa => new OpcUaDriver(Map()),
            PlcProtocolType.EtherNetIp => new EtherNetIpDriver(Map()),
            PlcProtocolType.S7 => new S7Driver(Map()),
            _ => throw new NotSupportedException($"{protocol} driver is planned for release 3."),
        };
    }

    public static int DefaultPort(PlcProtocolType protocol) => protocol switch
    {
        PlcProtocolType.ModbusTcp => 502,
        PlcProtocolType.OpcUa => 4840,
        PlcProtocolType.OmronFins => 9600,
        PlcProtocolType.EtherNetIp => 44818,
        PlcProtocolType.S7 => 102,
        PlcProtocolType.Ads => 48898,
        _ => 0,
    };
}

/// <summary>Controller Connection panel helpers (spec §9.5): IPv4 validation and subnet hint.</summary>
public static class NetworkHint
{
    public static bool TryParseIPv4(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Split('.') is not { Length: 4 } parts || parts.Any(p => !byte.TryParse(p, out _) || p.Length > 3))
            return false;
        address = IPAddress.Parse(text);
        return true;
    }

    public static IReadOnlyList<(IPAddress Address, IPAddress Mask)> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(u => (u.Address, u.IPv4Mask))
            .ToList();

    /// <summary>Null when the PLC is on a local subnet; otherwise a warning for the operator.</summary>
    public static string? Check(IPAddress plc, IReadOnlyList<(IPAddress Address, IPAddress Mask)> local)
    {
        uint target = ToUInt(plc);
        if (local.Any(l => (ToUInt(l.Address) & ToUInt(l.Mask)) == (target & ToUInt(l.Mask))))
            return null;
        string pcs = local.Count == 0 ? "none" : string.Join(", ", local.Select(l => l.Address));
        return $"PLC {plc} is not on any PC subnet (PC: {pcs}). Give the flume NIC a static IP on the PLC subnet " +
               "and allow the protocol port through Windows Firewall.";
    }

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }
}
