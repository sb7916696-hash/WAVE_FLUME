using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaveFlume.App.Services;
using WaveFlume.Data;

namespace WaveFlume.App.ViewModels;

public sealed record CalibrationPoint(double RawMa, double KnownM);

public sealed record CalibrationRow(string Channel, string Gain, string Offset, string RSquared, string Date);

/// <summary>
/// "Channels and Calibrations" (spec §10): dip a probe at ≥ 3 known elevations, capture the averaged loop
/// current at each, fit η = gain·mA + offset by least squares, review R² and residuals, then save.
/// </summary>
public sealed partial class CalibrationViewModel : ObservableObject
{
    /// <summary>Averaging window for one dip point.</summary>
    public const double CaptureSeconds = 2.0;

    private readonly AppState _app;
    private readonly PlcSession _session;
    private readonly IDialogService _dialogs;
    private ProbeChannelCalibration? _fit;

    public CalibrationViewModel(AppState app, PlcSession session, IDialogService dialogs)
    {
        (_app, _session, _dialogs) = (app, session, dialogs);
        Channels = Enumerable.Range(1, WaveFlume.Plc.Signals.MaxProbes).Select(i => $"Probe {i}").ToList();
        RefreshRows();
        app.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.Calibration) or nameof(AppState.Settings)) RefreshRows();
        };
    }

    public IReadOnlyList<string> Channels { get; }
    public ObservableCollection<CalibrationPoint> Points { get; } = [];
    public ObservableCollection<CalibrationRow> Rows { get; } = [];

    [ObservableProperty] private int _channel;
    [ObservableProperty] private double _knownM;
    [ObservableProperty] private CalibrationPoint? _selectedPoint;
    [ObservableProperty] private string _fitText = $"Dip the probe at ≥ {ProbeCalibration.MinPoints} known elevations and capture each.";

    partial void OnChannelChanged(int value) => ClearPoints();

    [RelayCommand]
    private void Capture()
    {
        var telemetry = _session.Telemetry;
        if (telemetry is null)
        {
            _dialogs.Error("Calibration", "Connect to the controller first (Wave Paddle tab).");
            return;
        }
        double fs = _app.Settings.Flume.Limits.TelemetryRateHz;
        var raw = telemetry.UiBuffer.Latest((int)(CaptureSeconds * fs))
            .Where(s => s.ProbeRawMa.Length > Channel).Select(s => (double)s.ProbeRawMa[Channel]).ToList();
        if (raw.Count == 0)
        {
            _dialogs.Error("Calibration", "No raw probe current received for this channel.");
            return;
        }
        double mean = raw.Average();
        if (ProbeCalibration.IsLoopFault(mean))
        {
            _dialogs.Error("Calibration", $"Loop fault: {mean:0.00} mA. Check the probe wiring before calibrating.");
            return;
        }
        Points.Add(new CalibrationPoint(mean, KnownM));
        _fit = null;
        FitText = $"{Points.Count} point(s). Last: {mean:0.000} mA at {KnownM:0.000} m.";
    }

    [RelayCommand]
    private void RemovePoint()
    {
        if (SelectedPoint is { } p) Points.Remove(p);
        _fit = null;
    }

    [RelayCommand]
    private void ClearPoints()
    {
        Points.Clear();
        _fit = null;
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Fit()
    {
        try
        {
            _fit = ProbeCalibration.Fit(Channel, Points.Select(p => (p.RawMa, p.KnownM)).ToList(), DateTime.UtcNow);
            FitText = $"η = {_fit.GainMPerMa:0.000000} m/mA × I {_fit.OffsetM:+0.00000;-0.00000} m;  R² = {_fit.RSquared:0.00000};  " +
                      $"max |residual| = {_fit.ResidualsM.Max(Math.Abs) * 1000:0.0} mm";
        }
        catch (ArgumentException ex)
        {
            _fit = null;
            FitText = ex.Message;
        }
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => _fit is not null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_fit is null) return;
        _app.SaveCalibration(_app.Calibration.With(_fit));
        FitText = $"Saved calibration for {Channels[_fit.Channel]}.";
        ClearPoints();
    }

    [RelayCommand]
    private void ResetToNominal()
    {
        if (_dialogs.Confirm("Calibration", $"Return {Channels[Channel]} to the nominal range?"))
            _app.SaveCalibration(_app.Calibration.Without(Channel));
    }

    private void RefreshRows()
    {
        Rows.Clear();
        var nominal = _app.Settings.Flume.Probes;
        for (int ch = 0; ch < nominal.ActiveCount; ch++)
        {
            var (g, o) = _app.Calibration.For(ch, nominal);
            var c = _app.Calibration.Channels.FirstOrDefault(x => x.Channel == ch);
            Rows.Add(new CalibrationRow(Channels[ch], g.ToString("0.000000"), o.ToString("+0.00000;-0.00000"),
                c is null ? "nominal" : c.RSquared.ToString("0.00000"), c?.CalibratedUtc.ToLocalTime().ToString("yyyy-MM-dd") ?? "-"));
        }
    }
}
