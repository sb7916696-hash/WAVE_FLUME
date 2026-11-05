using System.Text.RegularExpressions;
using libplctag;

namespace WaveFlume.Plc;

/// <summary>
/// EtherNet/IP CIP via libplctag: Omron NX/NJ (default) or Rockwell ControlLogix. Addresses are PLC tag
/// names (<c>Wave.Period</c>, <c>Wave.Amp</c>); arrays read the whole array tag.
/// Extras: <see cref="PlcTypeKey"/> = "omron-njnx" | "controllogix", <see cref="PathKey"/> (default "1,0").
/// </summary>
public sealed partial class EtherNetIpDriver(TagMap map) : TagMapDriverBase(map)
{
    public const string PlcTypeKey = "EipPlcType";
    public const string PathKey = "EipPath";

    /// <summary>CIP connected messaging limit (~500 bytes) — keeps a trajectory chunk in one request.</summary>
    public const int MaxWordsPerRequest = 240;

    private readonly Dictionary<string, Tag> _tags = [];

    /// <summary>
    /// libplctag.NET re-extracts plctag.dll next to the exe on every start by default, which fails in a
    /// read-only Program Files folder. When the installer has shipped the DLL, use it as-is.
    /// </summary>
    static EtherNetIpDriver()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "plctag.dll")))
            libplctag.NativeImport.plctag.ForceExtractLibrary = false;
    }

    /// <summary>Loads (and if needed extracts) the native library; the publish script uses it to obtain plctag.dll.</summary>
    public static void EnsureNativeLibrary() => libplctag.NativeImport.plctag.plc_tag_check_lib_version(2, 1, 0);

    public override PlcProtocolType Protocol => PlcProtocolType.EtherNetIp;
    public override DriverCapabilities Capabilities { get; } = new(MaxWordsPerRequest, true, false);

    /// <summary>Controller- or program-scoped (Program:Main.Tag) names, members and array indices.</summary>
    [GeneratedRegex(@"^(Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*(\[\d+\])?(\.[A-Za-z_][A-Za-z0-9_]*(\[\d+\])?)*$")]
    private static partial Regex TagName();

    public override string? CheckAddress(TagEntry tag) =>
        TagName().IsMatch(tag.Address) ? null : $"'{tag.Address}' is not a CIP tag name (e.g. Wave.Period).";

    protected override async Task ConnectCoreAsync(PlcEndpoint ep, CancellationToken ct)
    {
        // Creating and reading one tag is the protocol handshake (session + forward open).
        await ReadTagCoreAsync(Map.Get(Signals.StatusWord), ct).ConfigureAwait(false);
    }

    protected override Task DisconnectCoreAsync()
    {
        foreach (var t in _tags.Values) t.Dispose();
        _tags.Clear();
        return Task.CompletedTask;
    }

    protected override async Task<double[]> ReadTagCoreAsync(TagEntry entry, CancellationToken ct)
    {
        var tag = await TagFor(entry, ct).ConfigureAwait(false);
        await Call(() => tag.ReadAsync(ct)).ConfigureAwait(false);
        var values = new double[entry.Length];
        int size = ElementSize(entry.DataType);
        for (int i = 0; i < entry.Length; i++)
        {
            double raw = entry.DataType switch
            {
                TagDataType.Bool => tag.GetUInt8(i * size) != 0 ? 1 : 0,
                TagDataType.UInt16 => tag.GetUInt16(i * size),
                TagDataType.Int32 => tag.GetInt32(i * size),
                _ => tag.GetFloat32(i * size),
            };
            values[i] = entry.DataType == TagDataType.Bool ? raw : entry.ToEngineering(raw);
        }
        return values;
    }

    protected override async Task WriteTagCoreAsync(TagEntry entry, double[] values, CancellationToken ct)
    {
        var tag = await TagFor(entry, ct).ConfigureAwait(false);
        int size = ElementSize(entry.DataType);
        for (int i = 0; i < entry.Length; i++)
        {
            double raw = entry.ToRaw(values[i]);
            switch (entry.DataType)
            {
                case TagDataType.Bool: tag.SetUInt8(i * size, values[i] != 0 ? (byte)1 : (byte)0); break;
                case TagDataType.UInt16: tag.SetUInt16(i * size, checked((ushort)Math.Round(raw))); break;
                case TagDataType.Int32: tag.SetInt32(i * size, checked((int)Math.Round(raw))); break;
                default: tag.SetFloat32(i * size, (float)raw); break;
            }
        }
        await Call(() => tag.WriteAsync(ct)).ConfigureAwait(false);
    }

    /// <summary>CIP sizes: BOOL 1, UINT 2, DINT 4, REAL 4 bytes.</summary>
    public static int ElementSize(TagDataType t) => t switch
    {
        TagDataType.Bool => 1,
        TagDataType.UInt16 => 2,
        _ => 4,
    };

    private async Task<Tag> TagFor(TagEntry entry, CancellationToken ct)
    {
        if (_tags.TryGetValue(entry.Address, out var existing)) return existing;
        bool omron = !string.Equals(Endpoint.Extra(PlcTypeKey), "controllogix", StringComparison.OrdinalIgnoreCase);
        var tag = new Tag
        {
            Name = entry.Address,
            Gateway = $"{Endpoint.IpAddress}:{Endpoint.Port}",
            Path = Endpoint.Extra(PathKey) ?? "1,0",
            PlcType = omron ? PlcType.Omron : PlcType.ControlLogix,
            Protocol = libplctag.Protocol.ab_eip,
            ElementSize = ElementSize(entry.DataType),
            ElementCount = entry.Length,
            Timeout = TimeSpan.FromMilliseconds(Math.Max(Endpoint.TimeoutMs, 100)),
        };
        try
        {
            await Call(() => tag.InitializeAsync(ct)).ConfigureAwait(false);
        }
        catch
        {
            tag.Dispose();
            throw;
        }
        _tags[entry.Address] = tag;
        return tag;
    }

    private static async Task Call(Func<Task> op)
    {
        try { await op().ConfigureAwait(false); }
        catch (LibPlcTagException ex)
        {
            throw new PlcProtocolException(Reason(ex.Message), $"EtherNet/IP: {ex.Message}");
        }
    }

    /// <summary>libplctag reports a status name (ErrorTimeout, ErrorNotFound, …) in the message.</summary>
    public static FailureReason Reason(string status) => status switch
    {
        _ when status.Contains("Timeout", StringComparison.OrdinalIgnoreCase) => FailureReason.Timeout,
        _ when status.Contains("NotFound", StringComparison.OrdinalIgnoreCase) || status.Contains("NoMatch", StringComparison.OrdinalIgnoreCase)
            || status.Contains("BadParam", StringComparison.OrdinalIgnoreCase) => FailureReason.InvalidConfig,
        _ when status.Contains("NotAllowed", StringComparison.OrdinalIgnoreCase) => FailureReason.AccessDenied,
        _ when status.Contains("BadConnection", StringComparison.OrdinalIgnoreCase) || status.Contains("BadGateway", StringComparison.OrdinalIgnoreCase)
            || status.Contains("Open", StringComparison.OrdinalIgnoreCase) || status.Contains("Winsock", StringComparison.OrdinalIgnoreCase) => FailureReason.Refused,
        _ => FailureReason.ProtocolError,
    };
}
