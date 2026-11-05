using Opc.Ua;
using Opc.Ua.Client;

namespace WaveFlume.Plc;

/// <summary>
/// OPC UA client. Addresses are NodeIds (<c>ns=3;s="DB_Wave"."Period"</c>); arrays map to one array node.
/// Security from <see cref="EndpointExtras.OpcUaSecurity"/> ("None" default, "Sign", "SignAndEncrypt").
/// </summary>
public sealed class OpcUaDriver(TagMap map, string pkiRoot = "pki") : TagMapDriverBase(map)
{
    /// <summary>No register limit; bound the trajectory chunk size anyway.</summary>
    public const int MaxValuesPerWrite = 4096;
    public const int SessionTimeoutMs = 60_000;

    private ISession? _session;

    public override PlcProtocolType Protocol => PlcProtocolType.OpcUa;
    public override DriverCapabilities Capabilities { get; } = new(MaxValuesPerWrite, true, false);

    public override string? CheckAddress(TagEntry tag)
    {
        try { NodeId.Parse(tag.Address); return null; }
        catch (Exception) { return $"'{tag.Address}' is not a NodeId (e.g. ns=3;s=\"DB_Wave\".\"Period\")."; }
    }

    protected override async Task ConnectCoreAsync(PlcEndpoint ep, CancellationToken ct)
    {
        string security = ep.Extra(EndpointExtras.OpcUaSecurity) ?? "None";
        bool secure = !security.Equals("None", StringComparison.OrdinalIgnoreCase);
        var config = new ApplicationConfiguration
        {
            ApplicationName = "WaveFlume",
            ApplicationUri = $"urn:{System.Net.Dns.GetHostName()}:WaveFlume",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=WaveFlume",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = CertificateStoreType.Directory, StorePath = Path.Combine(pkiRoot, "rejected") },
                // Secured sessions require the server certificate in pki/trusted; never auto-accept them.
                AutoAcceptUntrustedCertificates = !secure,
            },
            TransportQuotas = new TransportQuotas { OperationTimeout = ep.TimeoutMs },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = SessionTimeoutMs },
        };
        await config.Validate(ApplicationType.Client).ConfigureAwait(false);

        string url = $"opc.tcp://{ep.IpAddress}:{ep.Port}";
        var description = CoreClientUtils.SelectEndpoint(config, url, secure, ep.TimeoutMs);
        if (secure && !description.SecurityMode.ToString().Equals(security, StringComparison.OrdinalIgnoreCase))
            throw new PlcProtocolException(FailureReason.AccessDenied, $"Server offers no {security} endpoint.");
        var endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));

        string? user = ep.Extra(EndpointExtras.OpcUaUser);
        var identity = user is null
            ? new UserIdentity(new AnonymousIdentityToken())
            : new UserIdentity(user, ep.Extra(EndpointExtras.OpcUaPassword) ?? "");
        try
        {
            _session = await Opc.Ua.Client.Session.Create(config, endpoint, false, "WaveFlume", SessionTimeoutMs, identity, null).ConfigureAwait(false);
        }
        catch (ServiceResultException ex)
        {
            throw Translate(ex);
        }
    }

    protected override async Task DisconnectCoreAsync()
    {
        if (_session is null) return;
        try { await _session.CloseAsync().ConfigureAwait(false); }
        catch (ServiceResultException) { /* already gone */ }
        _session.Dispose();
        _session = null;
    }

    protected override async Task<double[]> ReadTagCoreAsync(TagEntry tag, CancellationToken ct)
    {
        DataValue dv;
        try { dv = await Session.ReadValueAsync(NodeId.Parse(tag.Address), ct).ConfigureAwait(false); }
        catch (ServiceResultException ex) { throw Translate(ex); }
        if (StatusCode.IsBad(dv.StatusCode))
            throw Translate(new ServiceResultException(dv.StatusCode));

        double[] raw = dv.Value switch
        {
            Array a => a.Cast<object>().Select(Convert.ToDouble).ToArray(),
            null => throw new PlcProtocolException(FailureReason.ProtocolError, $"{tag.Signal}: null value."),
            var v => [Convert.ToDouble(v)],
        };
        if (raw.Length != tag.Length)
            throw new PlcProtocolException(FailureReason.InvalidConfig, $"{tag.Signal}: node has {raw.Length} elements, TagMap says {tag.Length}.");
        return tag.DataType == TagDataType.Bool ? raw : Array.ConvertAll(raw, tag.ToEngineering);
    }

    protected override async Task WriteTagCoreAsync(TagEntry tag, double[] values, CancellationToken ct)
    {
        var raw = Array.ConvertAll(values, tag.ToRaw);
        object value = (tag.DataType, tag.Length) switch
        {
            (TagDataType.Bool, 1) => raw[0] != 0,
            (TagDataType.Bool, _) => Array.ConvertAll(raw, r => r != 0),
            (TagDataType.UInt16, 1) => checked((ushort)Math.Round(raw[0])),
            (TagDataType.UInt16, _) => Array.ConvertAll(raw, r => checked((ushort)Math.Round(r))),
            (TagDataType.Int32, 1) => checked((int)Math.Round(raw[0])),
            (TagDataType.Int32, _) => Array.ConvertAll(raw, r => checked((int)Math.Round(r))),
            (TagDataType.Float32, 1) => (float)raw[0],
            _ => Array.ConvertAll(raw, r => (float)r),
        };
        var write = new WriteValueCollection
        {
            new WriteValue { NodeId = NodeId.Parse(tag.Address), AttributeId = Attributes.Value, Value = new DataValue(new Variant(value)) },
        };
        WriteResponse response;
        try { response = await Session.WriteAsync(null, write, ct).ConfigureAwait(false); }
        catch (ServiceResultException ex) { throw Translate(ex); }
        if (StatusCode.IsBad(response.Results[0]))
            throw Translate(new ServiceResultException(response.Results[0]));
    }

    private ISession Session => _session ?? throw new PlcProtocolException(FailureReason.ProtocolError, "Not connected.");

    private static PlcProtocolException Translate(ServiceResultException ex)
    {
        uint code = ex.StatusCode;
        var reason = code switch
        {
            StatusCodes.BadUserAccessDenied or StatusCodes.BadIdentityTokenInvalid or StatusCodes.BadIdentityTokenRejected
                or StatusCodes.BadSecurityChecksFailed or StatusCodes.BadCertificateUntrusted or StatusCodes.BadNotWritable
                or StatusCodes.BadNotReadable => FailureReason.AccessDenied,
            StatusCodes.BadTimeout or StatusCodes.BadRequestTimeout => FailureReason.Timeout,
            StatusCodes.BadNotConnected or StatusCodes.BadConnectionRejected or StatusCodes.BadServerNotConnected => FailureReason.Refused,
            StatusCodes.BadNodeIdUnknown or StatusCodes.BadNodeIdInvalid or StatusCodes.BadTypeMismatch => FailureReason.InvalidConfig,
            _ => FailureReason.ProtocolError,
        };
        return new PlcProtocolException(reason, $"OPC UA {StatusCodes.GetBrowseName(code)}: {ex.Message}");
    }
}
