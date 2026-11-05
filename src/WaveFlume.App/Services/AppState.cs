using CommunityToolkit.Mvvm.ComponentModel;
using WaveFlume.Data;
using WaveFlume.Plc;

namespace WaveFlume.App.Services;

/// <summary>Current settings and the active TagMap, shared by all tabs.</summary>
public sealed partial class AppState : ObservableObject
{
    public AppState(SettingsStore store)
    {
        Store = store;
        _settings = store.Load();
        _calibration = CalibrationSet.Load(store.CalibrationPath);
        ReloadTagMap();
    }

    /// <summary>Probe calibrations (raw mA → m); read live by the telemetry service.</summary>
    [ObservableProperty]
    private CalibrationSet _calibration;

    public void SaveCalibration(CalibrationSet set)
    {
        set.Save(Store.CalibrationPath);
        Calibration = set;
    }

    public SettingsStore Store { get; }

    [ObservableProperty]
    private AppSettings _settings;

    public TagMap? TagMap { get; private set; }

    /// <summary>Connect stays disabled while this is non-empty (spec §9.5).</summary>
    public IReadOnlyList<string> TagMapErrors { get; private set; } = [];

    public void Save(AppSettings settings)
    {
        Store.Save(settings);
        Settings = settings;
        ReloadTagMap();
    }

    /// <summary>Applies a viewing preference (theme) without validating or reloading the TagMap.</summary>
    public void SavePreference(AppSettings settings)
    {
        Store.Save(settings);
        Settings = settings;
    }

    public void ReloadTagMap()
    {
        var c = Settings.Connection;
        TagMap = c.Protocol == PlcProtocolType.Simulator ? null : Store.LoadProfile(c.TagMapProfile);
        TagMapErrors = Validate(c.Protocol, TagMap, c.TagMapProfile);
        OnPropertyChanged(nameof(TagMapErrors));
        OnPropertyChanged(nameof(TagMap));
    }

    public static IReadOnlyList<string> Validate(PlcProtocolType protocol, TagMap? map, string profileName)
    {
        if (protocol == PlcProtocolType.Simulator) return [];
        if (map is null) return [$"TagMap profile '{profileName}' not found."];
        try { return ((TagMapDriverBase)DriverFactory.Create(protocol, map)).ValidateTagMap(); }
        catch (NotSupportedException ex) { return [ex.Message]; }
    }
}
