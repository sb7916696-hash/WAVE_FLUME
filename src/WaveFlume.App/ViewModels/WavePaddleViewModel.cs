using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaveFlume.App.Services;
using WaveFlume.Core;
using WaveFlume.Data;
using WaveFlume.Physics;
using WaveFlume.Plc;
using WaveFlume.Reporting;

namespace WaveFlume.App.ViewModels;

public sealed partial class PaddleIndicator(string name) : ObservableObject
{
    public const double WarnFraction = 0.80;
    public const double AlarmFraction = 0.95;

    public string Name { get; } = name;
    [ObservableProperty] private double _mm;
    [ObservableProperty] private double _percent;

    /// <summary>"Normal", "Warning" (≥ 80 % of clamp) or "Alarm" (≥ 95 %).</summary>
    [ObservableProperty] private string _level = "Normal";

    public void Update(double mm, double clampMm)
    {
        Mm = mm;
        double f = Math.Abs(mm) / clampMm;
        Percent = Math.Min(100, f * 100);
        Level = f >= AlarmFraction ? "Alarm" : f >= WarnFraction ? "Warning" : "Normal";
    }
}

public sealed record StatRow(string Name, string Live, string Final);

/// <summary>Tab 2: connect, send, run, watch and record (spec §11.3).</summary>
public sealed partial class WavePaddleViewModel : ObservableObject
{
    public const double StatsWindowS = 60;
    public const double HighAmplitudeFraction = 0.8;

    private readonly AppState _app;
    private readonly WaveGenerationViewModel _waveGen;
    private readonly AlarmService _alarms;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;

    private WaveRecipe? _sentRecipe;
    private Task<long>? _csvWriter;
    private bool _runSeen;
    private DateTime? _captureEndUtc;
    private DateTime _lastStatsUtc;

    private DateTime _runStartUtc;
    private RunStatus _runStatus;

    public WavePaddleViewModel(AppState app, PlcSession session, WaveGenerationViewModel waveGen, AlarmService alarms,
        IDialogService dialogs, TimeProvider? time = null)
    {
        (_app, Session, _waveGen, _alarms, _dialogs) = (app, session, waveGen, alarms, dialogs);
        _time = time ?? TimeProvider.System;
        _extraRecordS = app.Settings.ExtraRecordS;
        for (int i = 1; i <= app.Settings.Flume.NumPaddles; i++) Paddles.Add(new PaddleIndicator($"P{i}"));
        Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlcSession.State)) RefreshCommands();
        };
        waveGen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WaveGenerationViewModel.CanSend)) SendCommand.NotifyCanExecuteChanged();
        };
        BuildStats(null, null);
    }

    public PlcSession Session { get; }
    public ObservableCollection<PaddleIndicator> Paddles { get; } = [];
    public ObservableCollection<StatRow> Stats { get; } = [];

    [ObservableProperty] private double _extraRecordS;
    [ObservableProperty] private bool _daqStream = true;
    [ObservableProperty] private double _uploadProgress;
    [ObservableProperty] private string _statusMessage = "Not connected.";
    [ObservableProperty] private int _statsChannel;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveTestDataCommand))] private string? _lastRunCsv;
    [ObservableProperty] private TestDataFile? _offlineData;
    [ObservableProperty] private bool _isBusy;

    public string ConnectLabel => Session.State == RunState.Disconnected ? "_Connect" : "_Disconnect";
    public string SentTestNumber => _sentRecipe?.TestNumber ?? "(none sent)";
    private bool IsCapturing => _csvWriter is not null;

    private bool Allowed(OperatorAction a) => !IsBusy && RunStateRules.IsAllowed(Session.State, a);

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(ConnectLabel));
        foreach (var c in new IRelayCommand[] { ConnectCommand, EnableServosCommand, HomeCommand, SendCommand, StartCommand, SmoothStopCommand, ResetCommand })
            c.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    // ---- commands ---------------------------------------------------------------------------

    private bool CanConnect() => !IsBusy && (Allowed(OperatorAction.Connect) || Allowed(OperatorAction.Disconnect));

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        try
        {
            if (Session.State != RunState.Disconnected)
            {
                await Session.DisconnectAsync();
                StatusMessage = "Disconnected.";
                return;
            }
            if (_app.TagMapErrors.Count > 0)
            {
                _dialogs.Error("Connect", $"TagMap fails validation — fix it in Settings first:\n{_app.TagMapErrors[0]}");
                return;
            }
            StatusMessage = "Connecting...";
            var r = await Session.ConnectAsync(_app.Settings, _app.TagMap, CancellationToken.None);
            StatusMessage = r.Ok ? $"Connected (RTT {r.RttMs:0.0} ms)." : $"Connect failed: {r.Reason} — {r.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableServosAsync() => Command(PlcControlCommand.Enable, "Servos enabled.");
    private bool CanEnable() => Allowed(OperatorAction.EnableServos);

    [RelayCommand(CanExecute = nameof(CanHome))]
    private Task HomeAsync() => Command(PlcControlCommand.Home, "Paddles are at the starting position.");
    private bool CanHome() => Allowed(OperatorAction.Home);

    private bool CanSendNow() => Allowed(OperatorAction.Send) && _waveGen.CanSend;

    [RelayCommand(CanExecute = nameof(CanSendNow))]
    private async Task SendAsync()
    {
        if (_waveGen.Result is not { Safety.Ok: true } result) return;
        IsBusy = true;
        try
        {
            StatusMessage = "Uploading trajectory...";
            var progress = new Progress<double>(p => UploadProgress = p * 100);
            bool ok = await Session.UploadAsync(result.Trajectory, _waveGen.WaterDepthM, progress, CancellationToken.None);
            if (ok)
            {
                _sentRecipe = new WaveRecipe { TestNumber = _waveGen.TestNumber, Request = _waveGen.Request };
                OnPropertyChanged(nameof(SentTestNumber));
            }
            StatusMessage = ok ? $"Trajectory loaded: {result.Trajectory.SampleCount} samples x {result.Trajectory.PaddleCount} paddles." : "Upload failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var flume = _app.Settings.Flume;
        double clamp = Math.Min(flume.Limits.MaxStrokeM, HardLimits.MaxStrokeCeilingM);
        double worst = _waveGen.Result?.Safety.Worst.MaxAbsPositionM ?? 0;
        if (worst > HighAmplitudeFraction * clamp &&
            !_dialogs.Confirm("Start Wave", $"Paddle stroke reaches {Units.MToMm(worst):0} mm ({worst / clamp:P0} of the ±{Units.MToMm(clamp):0} mm clamp). Start?"))
            return;

        string testNumber = _sentRecipe?.TestNumber ?? _waveGen.TestNumber;
        var start = _time.GetUtcNow().UtcDateTime;
        string path = Path.Combine(_app.Settings.RunsDirectory, CsvLogger.RunFileName(start, testNumber));
        BeginCapture(path, testNumber, start);
        if (!await Session.CommandAsync(PlcControlCommand.Start))
        {
            Session.Telemetry?.EndCsvCapture();
            await (_csvWriter ?? Task.FromResult(0L));
            _csvWriter = null;
            StatusMessage = "Start refused.";
            return;
        }
        _runSeen = false;
        _captureEndUtc = null;
        _runStartUtc = start;
        _runStatus = RunStatus.Completed;
        StatusMessage = $"Running. Logging to {path}";
    }

    private bool CanStart() => Allowed(OperatorAction.Start) && !IsCapturing;

    [RelayCommand(CanExecute = nameof(CanSmoothStop))]
    private Task SmoothStopAsync() => Command(PlcControlCommand.SmoothStop, "Smooth stop commanded.");
    private bool CanSmoothStop() => Allowed(OperatorAction.SmoothStop);

    /// <summary>Always enabled: if not connected, says so — the hardwired chain is the primary stop.</summary>
    [RelayCommand]
    private async Task EStopAsync()
    {
        if (Session.Driver is null)
        {
            _alarms.Raise(AlertSeverity.EStop, AlertSources.PlcStatus, "Software E-STOP pressed while not connected — use the hardwired E-stop.");
            return;
        }
        _alarms.Raise(AlertSeverity.EStop, AlertSources.PlcStatus, "Software E-STOP pressed by operator.");
        if (IsCapturing) _runStatus = RunStatus.EStopped;
        await Session.CommandAsync(PlcControlCommand.EStop);
        StatusMessage = "E-STOP commanded.";
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private Task ResetAsync() => Command(PlcControlCommand.Reset, "Fault reset.");
    private bool CanReset() => Allowed(OperatorAction.Reset);

    private async Task Command(PlcControlCommand cmd, string done)
    {
        IsBusy = true;
        try { StatusMessage = await Session.CommandAsync(cmd) ? done : $"{cmd} refused."; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasRun))]
    private void SaveTestData()
    {
        if (LastRunCsv is null) return;
        var target = _dialogs.SaveFile("CSV (*.csv)|*.csv", Path.GetFileName(LastRunCsv));
        if (target is null || Path.GetFullPath(target) == Path.GetFullPath(LastRunCsv)) return;
        File.Copy(LastRunCsv, target, true);
        StatusMessage = $"Saved {target}";
    }
    private bool HasRun() => LastRunCsv is not null && File.Exists(LastRunCsv);

    [RelayCommand]
    private void OpenTestData()
    {
        var path = _dialogs.OpenFile("Run CSV (*.csv)|*.csv", _app.Settings.RunsDirectory);
        if (path is null) return;
        try
        {
            OfflineData = TestDataFile.Load(path);
            OfflineCsvPath = path;
            BuildStats(null, FinalStats(OfflineData, 0, 0));
            StatusMessage = $"Opened {Path.GetFileName(path)}: {OfflineData.Data[0].Length} samples.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException)
        {
            _dialogs.Error("Open Test Data", ex.Message);
        }
    }

    [RelayCommand]
    private void ShowLive()
    {
        OfflineData = null;
        OfflineCsvPath = null;
    }

    // ---- run lifecycle ----------------------------------------------------------------------

    private void BeginCapture(string path, string testNumber, DateTime startUtc)
    {
        var telemetry = Session.Telemetry ?? throw new InvalidOperationException("Not connected.");
        var s = _app.Settings;
        var header = new Dictionary<string, string>
        {
            ["test_number"] = testNumber,
            ["start_utc"] = startUtc.ToString("O"),
            ["recipe"] = JsonSerializer.Serialize(_sentRecipe?.Request ?? _waveGen.Request, FlumeConfig.JsonOptions).ReplaceLineEndings(" "),
            ["units"] = "t_s s; paddle mm; probe m",
            ["calibration_factor"] = s.CalibrationFactor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["probe_calibration"] = _app.Calibration.Snapshot(s.Flume.Probes, s.Flume.Probes.ActiveCount),
            ["limits"] = JsonSerializer.Serialize(s.Flume.Limits, FlumeConfig.JsonOptions).ReplaceLineEndings(" "),
            ["controller"] = $"{s.Connection.Protocol} {s.Connection.IpAddress}",
            ["app_version"] = typeof(WavePaddleViewModel).Assembly.GetName().Version?.ToString() ?? "",
        };
        int probes = Session.Latest?.ProbeM.Length ?? 0;
        _csvWriter = CsvLogger.WriteAsync(telemetry.BeginCsvCapture(), path, header, s.Flume.NumPaddles, probes, CancellationToken.None);
        LastRunCsv = path;
        RefreshCommands();
    }

    /// <summary>Called by the UI timer (~10 Hz).</summary>
    public async Task TickAsync()
    {
        Session.Refresh();
        var latest = Session.Latest;
        double clampMm = Units.MToMm(Math.Min(_app.Settings.Flume.Limits.MaxStrokeM, HardLimits.MaxStrokeCeilingM));
        if (latest is not null)
            for (int i = 0; i < Paddles.Count && i < latest.PaddleMm.Length; i++) Paddles[i].Update(latest.PaddleMm[i], clampMm);

        var now = _time.GetUtcNow().UtcDateTime;
        if (IsCapturing)
        {
            bool moving = Session.State is RunState.Running or RunState.Stopping;
            if (Session.State == RunState.Faulted && _runStatus == RunStatus.Completed) _runStatus = RunStatus.Fault;
            _runSeen |= moving;
            if (_runSeen && !moving && _captureEndUtc is null) _captureEndUtc = now.AddSeconds(ExtraRecordS);
            if (Session.Telemetry is null || (_captureEndUtc is { } end && now >= end)) await FinishCaptureAsync();
        }

        if (now - _lastStatsUtc >= TimeSpan.FromSeconds(1) && OfflineData is null)
        {
            _lastStatsUtc = now;
            BuildStats(LiveStats(), _finalStats);
        }
    }

    private ZeroCrossingStats? _finalStats;

    private async Task FinishCaptureAsync()
    {
        Session.Telemetry?.EndCsvCapture();
        long rows = await (_csvWriter ?? Task.FromResult(0L));
        _csvWriter = null;
        RefreshCommands();
        SaveTestDataCommand.NotifyCanExecuteChanged();
        if (LastRunCsv is null) return;
        try
        {
            double ramp = _sentRecipe?.Request.RampS ?? 0;
            _finalStats = FinalStats(TestDataFile.Load(LastRunCsv), ramp, ramp + ExtraRecordS);
            BuildStats(LiveStats(), _finalStats);
            StatusMessage = $"Run complete ({_runStatus}): {rows} samples in {Path.GetFileName(LastRunCsv)}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException)
        {
            StatusMessage = $"Run saved, statistics failed: {ex.Message}";
        }

        await GenerateReportAsync(LastRunCsv, _runStartUtc, _sentRecipe?.Request, _sentRecipe?.TestNumber ?? _waveGen.TestNumber);
    }

    /// <summary>PDF next to the CSV (spec §13), built off the UI thread.</summary>
    private async Task<string?> GenerateReportAsync(string csvPath, DateTime startUtc, WaveRequest? request, string testNumber)
    {
        string pdf = Path.ChangeExtension(csvPath, ".pdf");
        var alerts = _alarms.Log.Where(a => a.Alert.UtcTime >= startUtc).Select(a => a.Alert).Reverse().ToList();
        var probes = _app.Settings.Flume.Probes;
        var input = new ReportInput
        {
            CsvPath = csvPath, TestNumber = testNumber, StartUtc = startUtc, Request = request, Flume = _app.Settings.Flume,
            Alerts = alerts, CalibrationSnapshot = _app.Calibration.Snapshot(probes, probes.ActiveCount),
            SkipStartS = request?.RampS ?? 0, SkipEndS = (request?.RampS ?? 0) + ExtraRecordS,
        };
        var progress = new Progress<double>(p => StatusMessage = $"Generating report... {p:P0}");
        try
        {
            await Task.Run(() => PdfReportService.Generate(input, pdf, progress));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            StatusMessage = $"Report failed: {ex.Message}";
            return null;
        }
        LastReport = pdf;
        StatusMessage = $"Report: {pdf}";
        return pdf;
    }

    [ObservableProperty] private string? _lastReport;

    private string? OfflineCsvPath { get; set; }

    /// <summary>Report for the opened test data file, or else the last run.</summary>
    [RelayCommand]
    private async Task ReportAsync()
    {
        string? csv = OfflineCsvPath ?? LastRunCsv;
        if (csv is null || !File.Exists(csv))
        {
            _dialogs.Error("PDF Report", "No run or test data file to report on.");
            return;
        }
        var data = TestDataFile.Load(csv);
        WaveRequest? request = data.Header.TryGetValue("recipe", out var json)
            ? JsonSerializer.Deserialize<WaveRequest>(json, FlumeConfig.JsonOptions)
            : null;
        var start = data.Header.TryGetValue("start_utc", out var st)
                    && DateTime.TryParse(st, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d
            : File.GetCreationTimeUtc(csv);
        string testNumber = data.Header.GetValueOrDefault("test_number") ?? Path.GetFileNameWithoutExtension(csv);
        string? pdf = await GenerateReportAsync(csv, start, request, testNumber);
        if (pdf is null) _dialogs.Error("PDF Report", StatusMessage);
        else
            _dialogs.Info("PDF Report",
                $"This wave report is saved as a PDF report in this location:\n{Path.GetDirectoryName(pdf)}\n\nwith the name:\n{Path.GetFileName(pdf)}");
    }

    private ZeroCrossingStats? FinalStats(TestDataFile data, double skipStartS, double skipEndS)
    {
        string column = $"probe{StatsChannel + 1}_m";
        if (!data.Columns.Contains(column) || data.Data[0].Length < 10) return null;
        return WaveStatistics.ZeroUpcrossing(data.Column(column), data.SampleRateHz, skipStartS, skipEndS);
    }

    private ZeroCrossingStats? LiveStats()
    {
        if (Session.Telemetry is null || !DaqStream) return null;
        double fs = _app.Settings.Flume.Limits.TelemetryRateHz;
        var window = Session.Telemetry.UiBuffer.Latest((int)(StatsWindowS * fs));
        if (window.Length < 10 || StatsChannel >= window[^1].ProbeM.Length) return null;
        return WaveStatistics.ZeroUpcrossing(window.Select(s => (double)s.ProbeM[StatsChannel]).ToArray(), fs);
    }

    partial void OnStatsChannelChanged(int value) => BuildStats(LiveStats(), OfflineData is null ? _finalStats : FinalStats(OfflineData, 0, 0));

    private void BuildStats(ZeroCrossingStats? live, ZeroCrossingStats? final)
    {
        static string F(ZeroCrossingStats? s, Func<ZeroCrossingStats, double> f, string fmt) =>
            s is { WaveCount: > 0 } ? f(s).ToString(fmt) : "--";
        Stats.Clear();
        Stats.Add(new("H1/3 (m)", F(live, s => s.H13M, "0.0000"), F(final, s => s.H13M, "0.0000")));
        Stats.Add(new("H10 (m)", F(live, s => s.H10M, "0.0000"), F(final, s => s.H10M, "0.0000")));
        Stats.Add(new("Hmax (m)", F(live, s => s.HmaxM, "0.0000"), F(final, s => s.HmaxM, "0.0000")));
        Stats.Add(new("T1/3 (s)", F(live, s => s.T13S, "0.000"), F(final, s => s.T13S, "0.000")));
        Stats.Add(new("T10 (s)", F(live, s => s.T10S, "0.000"), F(final, s => s.T10S, "0.000")));
        Stats.Add(new("Tmax (s)", F(live, s => s.TmaxS, "0.000"), F(final, s => s.TmaxS, "0.000")));
        Stats.Add(new("Waves", F(live, s => s.WaveCount, "0"), F(final, s => s.WaveCount, "0")));
    }
}
