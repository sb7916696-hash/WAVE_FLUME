using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using ScottPlot;
using WaveFlume.App.ViewModels;
using WaveFlume.Physics;

namespace WaveFlume.App.Views;

/// <summary>Plot rendering for the Wave Generation tab (presentation only; all numbers come from the engine).</summary>
public partial class WaveGenerationView : UserControl
{
    private WaveGenerationViewModel? _vm;

    public WaveGenerationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as WaveGenerationViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            Redraw();
        };
        // Tabs unload their content when deselected, so (re)subscribe on every Loaded.
        Loaded += (_, _) => { App.ThemeChanged += Redraw; Redraw(); };
        Unloaded += (_, _) => App.ThemeChanged -= Redraw;
        LimitPlot.MouseMove += OnLimitHover;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WaveGenerationViewModel.Result) or nameof(WaveGenerationViewModel.LimitPoints)) Redraw();
    }

    private void Redraw()
    {
        DrawLimit();
        DrawBoard();
        DrawElevation();
        DrawSpectrum();
    }

    private void DrawLimit()
    {
        var p = LimitPlot.Plot;
        p.Clear();
        PlotStyle.Apply(p, "Wave Period T (s)", "Wave Height H (m)");
        if (_vm is { LimitPoints.Count: > 0 } vm)
        {
            var curve = p.Add.Scatter(vm.LimitPoints.Select(l => l.PeriodS).ToArray(), vm.LimitPoints.Select(l => l.HeightM).ToArray(), PlotStyle.Trace(0));
            curve.MarkerSize = 0;
            curve.LineWidth = 1;
            curve.LegendText = "Operating limit H_lim(T)";
            bool inside = vm.Result?.Safety.Ok == true;
            var marker = p.Add.Marker(vm.PeriodS, vm.WaveHeightM, inside ? MarkerShape.FilledCircle : MarkerShape.Cross, 10,
                Color.FromHex(inside ? "#4C8A4C" : "#C00000"));
            marker.LegendText = inside ? "Operating point (inside)" : "Operating point (OUTSIDE)";
            p.ShowLegend(Alignment.UpperRight);
            p.Axes.AutoScale();
            p.Axes.SetLimitsY(0, Math.Max(vm.LimitPoints.Max(l => l.HeightM), vm.WaveHeightM) * 1.1);
        }
        LimitPlot.Refresh();
    }

    private void OnLimitHover(object sender, MouseEventArgs e)
    {
        if (_vm is not { LimitPoints.Count: > 0 } vm) return;
        var pos = e.GetPosition(LimitPlot);
        var c = LimitPlot.Plot.GetCoordinates(new Pixel(pos.X * LimitPlot.DisplayScale, pos.Y * LimitPlot.DisplayScale));
        var nearest = vm.LimitPoints.MinBy(l => Math.Abs(l.PeriodS - c.X));
        LimitHover.Text = $"T = {nearest.PeriodS:0.000} s   H_lim = {nearest.HeightM:0.0000} m   active limit: {nearest.Active}";
    }

    private void DrawBoard()
    {
        var p = BoardPlot.Plot;
        p.Clear();
        PlotStyle.Apply(p, "Time (s)", "Paddle Displacement (mm)");
        p.Title("");
        if (_vm?.Result is { } r)
        {
            var t = r.Trajectory;
            for (int k = 0; k < t.PaddleCount; k++)
            {
                var sig = p.Add.Signal(Array.ConvertAll(t.PositionsM[k], x => x * 1000.0), 1 / t.SampleRateHz, PlotStyle.Trace(k));
                // Paddle 1 is drawn thickest and paddle 6 thinnest, so every colour stays visible when the lines coincide.
                sig.LineWidth = PlotStyle.PaddleLineWidth(k, t.PaddleCount);
                sig.LegendText = $"Paddle {k + 1}";
            }
            if (_vm.AngleDeg == 0 && t.PaddleCount > 1)
                p.Title("All six paddles move together at a wave angle of 0, so their lines lie on top of each other.");
            p.ShowLegend(Alignment.UpperRight);
            p.Axes.AutoScale();
        }
        BoardPlot.Refresh();
    }

    private double[]? PredictedEta() => _vm?.Result?.PredictedEtaM;

    private void DrawElevation()
    {
        var p = ElevationPlot.Plot;
        p.Clear();
        PlotStyle.Apply(p, "Time (s)", "Predicted Elevation at Paddle (m)");
        if (PredictedEta() is { } eta && _vm is not null)
        {
            var sig = p.Add.Signal(eta, 1 / _vm.SamplingHz, PlotStyle.Trace(0));
            sig.LineWidth = 1;
            p.Axes.AutoScale();
        }
        ElevationPlot.Refresh();
    }

    private void DrawSpectrum()
    {
        var p = SpectrumPlot.Plot;
        p.Clear();
        PlotStyle.Apply(p, "Frequency (Hz)", "Spectral Density (m²/Hz)");
        if (PredictedEta() is { Length: > 64 } eta && _vm is not null)
        {
            var s = WaveStatistics.Spectral(eta, _vm.SamplingHz, Math.Min(eta.Length, 8192));
            double fMax = _vm.Result?.TargetSpectrum?.FrequencyHz[^1] ?? 4 / _vm.PeriodS;
            int n = s.FrequencyHz.TakeWhile(f => f <= fMax).Count();
            var sc = p.Add.Scatter(s.FrequencyHz[..n], s.Psd[..n], PlotStyle.Trace(0));
            sc.MarkerSize = 0;
            sc.LineWidth = 1;
            sc.LegendText = "Predicted elevation (Welch)";
            if (_vm.Result?.TargetSpectrum is { } target)
            {
                var t = p.Add.Scatter(target.FrequencyHz, target.DensityM2PerHz, PlotStyle.Trace(1));
                t.MarkerSize = 0;
                t.LineWidth = 1;
                t.LegendText = "Target S(f)";
                p.ShowLegend(Alignment.UpperRight);
            }
            p.Axes.AutoScale();
        }
        SpectrumPlot.Refresh();
    }
}
