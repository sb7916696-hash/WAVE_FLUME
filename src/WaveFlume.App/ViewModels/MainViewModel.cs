using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaveFlume.App.Services;
using WaveFlume.Core;

namespace WaveFlume.App.ViewModels;

/// <summary>Window shell: alarm banner, status bar, clock, and the UI tick that drives the tabs.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppState _app;
    private readonly IDialogService _dialogs;

    public MainViewModel(AppState app, AlarmService alarms, PlcSession session, IDialogService dialogs,
        WaveGenerationViewModel waveGeneration, WavePaddleViewModel wavePaddle, SettingsViewModel settings, CalibrationViewModel calibration)
    {
        _app = app;
        _dialogs = dialogs;
        (Alarms, Session, WaveGeneration, WavePaddle, Settings, Calibration) =
            (alarms, session, waveGeneration, wavePaddle, settings, calibration);
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.Settings)) OnPropertyChanged(nameof(AccelText));
        };
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlcSession.State)) OnPropertyChanged(nameof(RunStateText));
        };
        alarms.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AlarmService.Banner)) ShowBannerDetailsCommand.NotifyCanExecuteChanged();
        };
    }

    public AlarmService Alarms { get; }
    public PlcSession Session { get; }
    public WaveGenerationViewModel WaveGeneration { get; }
    public WavePaddleViewModel WavePaddle { get; }
    public SettingsViewModel Settings { get; }
    public CalibrationViewModel Calibration { get; }

    [ObservableProperty] private string _clock = "";

    public string RunStateText => $"Run: {StateLabel(Session.State)}";

    /// <summary>Plain wording for the run state shown in the status bar.</summary>
    public static string StateLabel(RunState state) => state switch
    {
        RunState.Disconnected => "Disconnected",
        RunState.Connected => "Connected",
        RunState.ServosEnabled => "Servos on",
        RunState.Homed => "At starting position",
        RunState.TrajectoryLoaded => "Wave loaded",
        RunState.Running => "Running",
        RunState.Stopping => "Stopping",
        RunState.Faulted => "Fault",
        _ => state.ToString(),
    };

    /// <summary>Spec §2: shown permanently while a_max is not configured.</summary>
    public string? AccelText => _app.Settings.Flume.Limits.MaxAccelMs2 is null ? "Acceleration limit not configured" : null;

    public bool IsRunning => Session.State is RunState.Running or RunState.Stopping;

    /// <summary>
    /// Opens the problem-and-solution dialog for the alarm on the alarm bar. An alarm is acknowledged only from
    /// inside that dialog.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasBannerAlarm))]
    private void ShowBannerDetails() => ShowAlarmDetails(Alarms.Banner);

    private bool HasBannerAlarm() => Alarms.Banner is not null;

    private static bool CanShowAlarmDetails(AlarmEntry? entry) => entry is not null;

    /// <summary>Details for one alarm of the log. Greyed out until a row is selected.</summary>
    [RelayCommand(CanExecute = nameof(CanShowAlarmDetails))]
    private void ShowAlarmDetails(AlarmEntry? entry)
    {
        if (entry is null) return;
        var advice = AlarmAdvice.For(entry.Alert);
        bool acknowledge = _dialogs.ShowAlarmDetails(new AlarmDetails(
            entry.Priority, entry.Time, entry.Source, entry.Description, advice.Problem, advice.Solution,
            entry.Severity != AlertSeverity.Warning, entry.Acknowledged));
        if (acknowledge) Alarms.Acknowledge(entry);
    }

    [RelayCommand]
    private void AcknowledgeAll() => Alarms.AcknowledgeAll();

    public async Task TickAsync()
    {
        Clock = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await WavePaddle.TickAsync();
    }
}
