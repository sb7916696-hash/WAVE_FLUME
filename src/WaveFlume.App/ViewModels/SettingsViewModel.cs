using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaveFlume.App.Services;
using WaveFlume.Core;
using WaveFlume.Data;
using WaveFlume.Plc;

namespace WaveFlume.App.ViewModels;

/// <summary>Editable TagMap row for the DataGrid.</summary>
public sealed partial class TagRow : ObservableObject
{
    [ObservableProperty] private string _signal = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private TagDataType _dataType;
    [ObservableProperty] private int _length = 1;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private double _offset;
    [ObservableProperty] private TagAccess _access;
    [ObservableProperty] private string _units = "";

    public static TagRow From(TagEntry t) => new()
    {
        Signal = t.Signal, Address = t.Address, DataType = t.DataType, Length = t.Length,
        Scale = t.Scale, Offset = t.Offset, Access = t.Access, Units = t.Units,
    };

    public TagEntry ToEntry() => new()
    {
        Signal = Signal, Address = Address, DataType = DataType, Length = Length,
        Scale = Scale, Offset = Offset, Access = Access, Units = Units,
    };
}

/// <summary>Tab 3: limits, ranges, controller connection, TagMap (spec §11.4, §9.5).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppState _app;
    private readonly IDialogService _dialogs;
    private readonly Func<IReadOnlyList<(System.Net.IPAddress, System.Net.IPAddress)>> _localIps;

    public SettingsViewModel(AppState app, IDialogService dialogs,
        Func<IReadOnlyList<(System.Net.IPAddress, System.Net.IPAddress)>>? localIps = null)
    {
        (_app, _dialogs) = (app, dialogs);
        _localIps = localIps ?? NetworkHint.LocalIPv4;
        _profiles = app.Store.ProfileFiles();
        LoadFrom(app.Settings);
    }

    public IReadOnlyList<PaddleType> PaddleTypes { get; } = Enum.GetValues<PaddleType>();
    public IReadOnlyList<PlcProtocolType> Protocols { get; } = Enum.GetValues<PlcProtocolType>();
    public IReadOnlyList<PlcTransport> Transports { get; } = Enum.GetValues<PlcTransport>();
    public IReadOnlyList<WordOrder> WordOrders { get; } = Enum.GetValues<WordOrder>();
    public IReadOnlyList<TagDataType> DataTypes { get; } = Enum.GetValues<TagDataType>();
    public IReadOnlyList<TagAccess> Accesses { get; } = Enum.GetValues<TagAccess>();
    public IReadOnlyList<ThemeKind> Themes { get; } = Enum.GetValues<ThemeKind>();
    public IReadOnlyList<DaqSourceKind> DaqSources { get; } = Enum.GetValues<DaqSourceKind>();
    public IReadOnlyList<string> OpcSecurityModes { get; } = ["None", "Sign", "SignAndEncrypt"];

    // ---- flume & limits (UI units: stroke in mm) --------------------------------------------

    [ObservableProperty] private PaddleType _paddleType;
    [ObservableProperty] private double _hingeHeightM;
    [ObservableProperty] private double _maxStrokeMm;
    [ObservableProperty] private double _maxVelocityMs;

    /// <summary>Blank = not configured (Send stays disabled).</summary>
    [ObservableProperty] private string _maxAccelMs2 = "";
    [ObservableProperty] private double _tMinS;
    [ObservableProperty] private double _tMaxS;
    [ObservableProperty] private double _commandFsHz;
    [ObservableProperty] private double _telemetryRateHz;
    [ObservableProperty] private double _maxWaterDepthM;
    [ObservableProperty] private double _waveBoardLocationM;
    [ObservableProperty] private double _calibrationFactor;
    [ObservableProperty] private double _extraRecordS;
    [ObservableProperty] private string _dataDirectory = "";
    public string PaddleWidthText => $"{_app.Settings.Flume.PaddleWidthM:0.00} m x {_app.Settings.Flume.NumPaddles}";
    public string CeilingText => $"Hard ceilings: stroke ±{Units.MToMm(HardLimits.MaxStrokeCeilingM):0} mm, velocity {HardLimits.MaxVelocityCeilingMs} m/s, period >= {HardLimits.AbsoluteMinPeriodS} s.";

    // Range bounds
    [ObservableProperty] private double _depthMin, _depthMax, _heightMin, _heightMax, _periodMin, _periodMax, _wavesMin, _wavesMax, _fsMin, _fsMax;

    // Display (operator may change; applies immediately)
    [ObservableProperty] private ThemeKind _theme;
    [ObservableProperty] private bool _oscilloscopePlots;

    // ---- controller connection --------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModbus), nameof(IsFins), nameof(IsOpcUa), nameof(IsS7), nameof(IsAds), nameof(UsesTagMap), nameof(UsesTransport), nameof(NetworkHintText))]
    private PlcProtocolType _protocol;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NetworkHintText), nameof(IpError))] private string _ipAddress = "";
    [ObservableProperty] private int _port;
    [ObservableProperty] private PlcTransport _transport;
    [ObservableProperty] private int _timeoutMs;
    [ObservableProperty] private int _retries;
    [ObservableProperty] private string _modbusUnitId = "1";
    [ObservableProperty] private string _finsDestNetwork = "", _finsDestNode = "", _finsDestUnit = "", _finsSourceNode = "";
    [ObservableProperty] private string _opcUaSecurity = "None";
    [ObservableProperty] private string _opcUaUser = "";
    [ObservableProperty] private string _s7Rack = "0", _s7Slot = "1", _adsNetId = "";
    [ObservableProperty] private DaqSourceKind _daqSource;
    [ObservableProperty] private string _externalDaqIp = "";
    [ObservableProperty] private string _testResult = "";
    private string? _protectedOpcPassword;

    public bool IsModbus => Protocol == PlcProtocolType.ModbusTcp;
    public bool IsFins => Protocol == PlcProtocolType.OmronFins;
    public bool IsOpcUa => Protocol == PlcProtocolType.OpcUa;
    public bool IsS7 => Protocol == PlcProtocolType.S7;
    public bool IsAds => Protocol == PlcProtocolType.Ads;
    public bool UsesTagMap => Protocol != PlcProtocolType.Simulator;
    public bool UsesTransport => Protocol == PlcProtocolType.OmronFins;

    public string? IpError => NetworkHint.TryParseIPv4(IpAddress, out _) ? null : "Not a valid IPv4 address.";

    public string NetworkHintText
    {
        get
        {
            if (!UsesTagMap || !NetworkHint.TryParseIPv4(IpAddress, out var ip)) return "";
            var local = _localIps();
            return NetworkHint.Check(ip, local) ?? $"PLC is on a PC subnet ({string.Join(", ", local.Select(l => l.Item1))}).";
        }
    }

    public void SetOpcUaPassword(string password) => _protectedOpcPassword = Secrets.Protect(password);

    partial void OnProtocolChanged(PlcProtocolType value)
    {
        Port = DriverFactory.DefaultPort(value);
        if (value == PlcProtocolType.OmronFins) TimeoutMs = 100; // spec §9.3
    }

    // ---- TagMap -----------------------------------------------------------------------------

    [ObservableProperty] private IReadOnlyList<string> _profiles;
    [ObservableProperty] private string _tagMapProfile = "";
    [ObservableProperty] private string _tagMapName = "";
    [ObservableProperty] private WordOrder _wordOrder;
    [ObservableProperty] private string _tagMapStatus = "";
    public ObservableCollection<TagRow> TagRows { get; } = [];

    partial void OnTagMapProfileChanged(string value) => LoadTagMap(_app.Store.LoadProfile(value));

    private void LoadTagMap(TagMap? map)
    {
        TagRows.Clear();
        if (map is null) { TagMapStatus = "Profile not found."; return; }
        TagMapName = map.Name;
        WordOrder = map.WordOrder;
        foreach (var t in map.Tags) TagRows.Add(TagRow.From(t));
        TagMapStatus = "";
    }

    private TagMap EditedTagMap() => new()
    {
        Name = TagMapName, Protocol = Protocol, WordOrder = WordOrder, Tags = TagRows.Select(r => r.ToEntry()).ToList(),
    };

    [RelayCommand]
    private void ValidateTags()
    {
        var errors = AppState.Validate(Protocol, EditedTagMap(), TagMapProfile);
        TagMapStatus = errors.Count == 0 ? "All tags valid." : $"{errors.Count} error(s): {string.Join(" | ", errors.Take(5))}";
    }

    [RelayCommand]
    private void ImportTags()
    {
        var path = _dialogs.OpenFile("TagMap JSON (*.json)|*.json");
        if (path is null) return;
        try { LoadTagMap(TagMap.Load(path)); ValidateTags(); }
        catch (Exception ex) when (ex is IOException or JsonException) { _dialogs.Error("Import TagMap", ex.Message); }
    }

    [RelayCommand]
    private void ExportTags()
    {
        var path = _dialogs.SaveFile("TagMap JSON (*.json)|*.json", TagMapProfile);
        if (path is not null) EditedTagMap().Save(path);
    }

    // ---- load / save ------------------------------------------------------------------------

    private void LoadFrom(AppSettings s)
    {
        var f = s.Flume;
        var l = f.Limits;
        var r = f.Ranges;
        PaddleType = f.PaddleType; HingeHeightM = f.HingeHeightM; MaxStrokeMm = Units.MToMm(l.MaxStrokeM); MaxVelocityMs = l.MaxVelocityMs;
        MaxAccelMs2 = l.MaxAccelMs2?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";
        TMinS = l.TMinS; TMaxS = l.TMaxS; CommandFsHz = l.CommandFsHz; TelemetryRateHz = l.TelemetryRateHz; MaxWaterDepthM = f.MaxWaterDepthM;
        WaveBoardLocationM = s.WaveBoardLocationM; CalibrationFactor = s.CalibrationFactor; ExtraRecordS = s.ExtraRecordS; DataDirectory = s.DataDirectory;
        (DepthMin, DepthMax) = (r.WaterDepthM.Min, r.WaterDepthM.Max);
        (HeightMin, HeightMax) = (r.WaveHeightM.Min, r.WaveHeightM.Max);
        (PeriodMin, PeriodMax) = (r.PeriodS.Min, r.PeriodS.Max);
        (WavesMin, WavesMax) = (r.NumWaves.Min, r.NumWaves.Max);
        (FsMin, FsMax) = (r.SamplingHz.Min, r.SamplingHz.Max);
        Theme = s.Theme; OscilloscopePlots = s.OscilloscopePlots;

        var c = s.Connection;
        Protocol = c.Protocol;
        IpAddress = c.IpAddress; Port = c.Port > 0 ? c.Port : DriverFactory.DefaultPort(c.Protocol);
        Transport = c.Transport; TimeoutMs = c.TimeoutMs; Retries = c.Retries;
        string X(string key, string fallback = "") => c.Extras.TryGetValue(key, out var v) ? v : fallback;
        ModbusUnitId = X(EndpointExtras.ModbusUnitId, "1");
        FinsDestNetwork = X(EndpointExtras.FinsDestNetwork); FinsDestNode = X(EndpointExtras.FinsDestNode);
        FinsDestUnit = X(EndpointExtras.FinsDestUnit); FinsSourceNode = X(EndpointExtras.FinsSourceNode);
        OpcUaSecurity = X(EndpointExtras.OpcUaSecurity, "None"); OpcUaUser = X(EndpointExtras.OpcUaUser);
        S7Rack = X("S7Rack", "0"); S7Slot = X("S7Slot", "1"); AdsNetId = X("AdsNetId");
        _protectedOpcPassword = c.ProtectedOpcUaPassword;
        DaqSource = c.DaqSource; ExternalDaqIp = c.ExternalDaqIp ?? "";
        TagMapProfile = c.TagMapProfile;
        LoadTagMap(_app.Store.LoadProfile(c.TagMapProfile));

        TestResult = "";
    }

    /// <summary>Builds settings from the form; null plus an error message when a field is invalid.</summary>
    public (AppSettings? Settings, string? Error) Build()
    {
        double? accel = null;
        if (!string.IsNullOrWhiteSpace(MaxAccelMs2))
        {
            if (!double.TryParse(MaxAccelMs2, out var a)) return (null, "Max acceleration must be a number or blank.");
            accel = a;
        }
        if (IpError is { } ipErr && Protocol != PlcProtocolType.Simulator) return (null, $"PLC IP: {ipErr}");

        var s = _app.Settings;
        var flume = s.Flume with
        {
            PaddleType = PaddleType, HingeHeightM = HingeHeightM, MaxWaterDepthM = MaxWaterDepthM,
            Limits = s.Flume.Limits with
            {
                MaxStrokeM = Units.MmToM(MaxStrokeMm), MaxVelocityMs = MaxVelocityMs, MaxAccelMs2 = accel,
                TMinS = TMinS, TMaxS = TMaxS, CommandFsHz = CommandFsHz, TelemetryRateHz = TelemetryRateHz,
            },
            Ranges = new RangeBounds
            {
                WaterDepthM = new(DepthMin, DepthMax), WaveHeightM = new(HeightMin, HeightMax), PeriodS = new(PeriodMin, PeriodMax),
                NumWaves = new(WavesMin, WavesMax), SamplingHz = new(FsMin, FsMax),
            },
        };
        var errors = flume.Validate();
        if (errors.Count > 0) return (null, string.Join("\n", errors));

        var extras = new Dictionary<string, string>();
        void Put(string key, string value) { if (!string.IsNullOrWhiteSpace(value)) extras[key] = value.Trim(); }
        Put(EndpointExtras.ModbusUnitId, ModbusUnitId);
        Put(EndpointExtras.FinsDestNetwork, FinsDestNetwork); Put(EndpointExtras.FinsDestNode, FinsDestNode);
        Put(EndpointExtras.FinsDestUnit, FinsDestUnit); Put(EndpointExtras.FinsSourceNode, FinsSourceNode);
        Put(EndpointExtras.OpcUaSecurity, OpcUaSecurity); Put(EndpointExtras.OpcUaUser, OpcUaUser);
        Put("S7Rack", S7Rack); Put("S7Slot", S7Slot); Put("AdsNetId", AdsNetId);

        return (s with
        {
            Flume = flume,
            WaveBoardLocationM = WaveBoardLocationM, CalibrationFactor = CalibrationFactor, ExtraRecordS = ExtraRecordS,
            DataDirectory = DataDirectory, Theme = Theme, OscilloscopePlots = OscilloscopePlots,
            Connection = new ConnectionSettings
            {
                Protocol = Protocol, IpAddress = IpAddress.Trim(), Port = Port, Transport = Transport, TimeoutMs = TimeoutMs,
                Retries = Retries, Extras = extras, ProtectedOpcUaPassword = _protectedOpcPassword, TagMapProfile = TagMapProfile,
                DaqSource = DaqSource, ExternalDaqIp = ExternalDaqIp,
            },
        }, null);
    }

    [RelayCommand]
    private void Save()
    {
        var (settings, error) = Build();
        if (settings is null) { _dialogs.Error("Save settings", error!); return; }
        if (UsesTagMap && TagRows.Count > 0)
        {
            var map = EditedTagMap();
            map.Save(Path.Combine(_app.Store.ProfilesDirectory, TagMapProfile));
        }
        _app.Save(settings);
        _dialogs.Info("Save settings", _app.TagMapErrors.Count == 0
            ? "Settings saved."
            : $"Settings saved, but the TagMap is invalid and Connect stays disabled:\n{_app.TagMapErrors[0]}");
    }

    [RelayCommand]
    private void Revert() => LoadFrom(_app.Settings);

    /// <summary>Theme and plot style are viewing preferences: applied and saved at once.</summary>
    partial void OnThemeChanged(ThemeKind value) => ApplyPreference();
    partial void OnOscilloscopePlotsChanged(bool value) => ApplyPreference();

    private void ApplyPreference()
    {
        if (_app.Settings.Theme == Theme && _app.Settings.OscilloscopePlots == OscilloscopePlots) return;
        _app.SavePreference(_app.Settings with { Theme = Theme, OscilloscopePlots = OscilloscopePlots });
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var (settings, error) = Build();
        if (settings is null) { TestResult = $"Invalid settings: {error}"; return; }
        TagMap? map = UsesTagMap ? EditedTagMap() : null;
        TestResult = "Testing...";
        var (c, ping) = await PlcSession.TestAsync(settings, map, CancellationToken.None);
        TestResult = c.Ok && ping is { Ok: true }
            ? $"OK: handshake RTT {c.RttMs:0.0} ms; StatusWord = {ping.TestTagValue}; ping {ping.RttMs:0.0} ms."
            : $"FAILED ({c.Reason}): {c.Message}";
    }
}
