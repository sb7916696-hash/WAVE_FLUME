using System.IO;
using System.Text.Json;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.App.Services;

/// <summary>
/// The choices made in the installer (controller type and address, data folder). The installer writes them to
/// <c>config\install_defaults.json</c>; the program applies them once, when it creates its settings for the first
/// time. Anything missing or invalid is ignored, so a damaged file can never stop the program from starting.
/// </summary>
public sealed record InstallDefaults
{
    public const string FileName = "install_defaults.json";

    /// <summary>Simulator, ModbusTcp, OpcUa, OmronFins, EtherNetIp or S7.</summary>
    public string? Protocol { get; init; }
    public string? IpAddress { get; init; }
    public int? Port { get; init; }

    /// <summary>Full path of the data folder. Empty keeps the default (Documents\WaveFlume).</summary>
    public string? DataDirectory { get; init; }

    public static InstallDefaults? Load(string configDirectory)
    {
        try
        {
            string path = Path.Combine(configDirectory, FileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<InstallDefaults>(File.ReadAllText(path), FlumeConfig.JsonOptions)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The TagMap profile that matches each protocol.</summary>
    public static string ProfileFor(PlcProtocolType protocol) => protocol switch
    {
        PlcProtocolType.ModbusTcp => "modbus_generic.json",
        PlcProtocolType.OpcUa => "opcua_generic.json",
        PlcProtocolType.EtherNetIp => "omron_eip_nx.json",
        PlcProtocolType.S7 => "siemens_s7_db.json",
        _ => "omron_fins_dm.json",
    };

    public AppSettings Apply(AppSettings settings)
    {
        var connection = settings.Connection;
        if (Enum.TryParse<PlcProtocolType>(Protocol, ignoreCase: true, out var protocol) && Enum.IsDefined(protocol))
        {
            connection = connection with { Protocol = protocol, TagMapProfile = ProfileFor(protocol), Port = Port is > 0 and <= 65535 ? Port.Value : 0 };
            if (protocol != PlcProtocolType.Simulator && NetworkHint.TryParseIPv4(IpAddress ?? "", out _))
                connection = connection with { IpAddress = IpAddress!.Trim() };
        }

        string dataDirectory = settings.DataDirectory;
        if (!string.IsNullOrWhiteSpace(DataDirectory) && Path.IsPathFullyQualified(DataDirectory))
            dataDirectory = DataDirectory.Trim();

        return settings with { Connection = connection, DataDirectory = dataDirectory };
    }
}

/// <summary>Where the user manual is, and the optional online link shown beside it in the Help tab.</summary>
public static class ManualLocator
{
    public const string PdfName = "Wave_Flume_User_Manual.pdf";
    public const string LinkFileName = "manual_link.txt";

    /// <summary>The PDF installed next to the program, or null when it is not there.</summary>
    public static string? FindPdf(string baseDirectory)
    {
        string path = Path.Combine(baseDirectory, "docs", PdfName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// First line of <c>config\manual_link.txt</c> that is not blank and not a "#" comment, when it is an http or
    /// https address. Null otherwise, which hides the online link.
    /// </summary>
    public static string? ReadOnlineLink(string configDirectory)
    {
        try
        {
            string path = Path.Combine(configDirectory, LinkFileName);
            if (!File.Exists(path)) return null;
            foreach (var raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                return Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
