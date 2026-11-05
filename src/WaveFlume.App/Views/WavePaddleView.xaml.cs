using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ScottPlot.WPF;
using WaveFlume.App.ViewModels;
using WaveFlume.Core;
using WaveFlume.Data;
using WaveFlume.Physics;
using WaveFlume.Plc;

namespace WaveFlume.App.Views;

/// <summary>
/// Oscilloscope rendering (spec §11.3): a 16 ms timer redraws only when new samples exist,
/// reusing the per-trace arrays once the window is full. ScottPlot's Signal plot decimates to
/// ≤ 2 points per pixel column.
/// </summary>
public partial class WavePaddleView : UserControl
{
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly int[] WindowSeconds = [10, 30, 60];

    private readonly DispatcherTimer _frame;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    private WavePaddleViewModel? _vm;
    private long _lastCounter = -1;
    private bool _dirty = true;
    private int _frames;
    private double[][] _buffers = [];

    public WavePaddleView()
    {
        InitializeComponent();
        var channels = Enumerable.Range(1, Signals.MaxProbes).Select(i => $"Ch {i}").ToArray();
        foreach (var (box, index) in new[] { (Ch1, 0), (Ch2, 1), (Ch3, 2) })
        {
            box.ItemsSource = channels;
            box.SelectedIndex = index;
            box.SelectionChanged += (_, _) => _dirty = true;
        }
        StatsChannelBox.ItemsSource = channels;
        foreach (var c in new Control[] { ShowBox, WindowBox, PlotTabs })
            if (c is Selector s) s.SelectionChanged += (_, e) => { if (e.OriginalSource == s) _dirty = true; };
        PauseBox.Click += (_, _) => _dirty = true;

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as WavePaddleViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
            _dirty = true;
        };
        _frame = new DispatcherTimer(FrameInterval, DispatcherPriority.Render, (_, _) => OnFrame(), Dispatcher);
        // Tabs unload their content when deselected, so (re)subscribe on every Loaded.
        Loaded += (_, _) => { App.ThemeChanged += MarkDirty; _dirty = true; _frame.Start(); };
        Unloaded += (_, _) => { _frame.Stop(); App.ThemeChanged -= MarkDirty; };
    }

    private void MarkDirty() => _dirty = true;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WavePaddleViewModel.OfflineData)) _dirty = true;
    }

    private bool Spectrum => ShowBox.SelectedIndex == 1;
    private int[] Channels => [Ch1.SelectedIndex, Ch2.SelectedIndex, Ch3.SelectedIndex];

    private void OnFrame()
    {
        if (_vm is null || !IsVisible) return;
        if (_vm.OfflineData is { } file)
        {
            if (_dirty) DrawOffline(file);
            _dirty = false;
            return;
        }
        if (PauseBox.IsChecked == true || _vm.Session.Telemetry is not { } telemetry) return;

        double fs = _vm.Session.Flume.Limits.TelemetryRateHz;
        var samples = telemetry.UiBuffer.Latest((int)(WindowSeconds[Math.Max(0, WindowBox.SelectedIndex)] * fs));
        if (samples.Length < 2 || (samples[^1].SampleCounter == _lastCounter && !_dirty)) return;
        _lastCounter = samples[^1].SampleCounter;
        _dirty = false;

        if (PlotTabs.SelectedIndex == 0)
        {
            int paddles = samples[^1].PaddleMm.Length;
            var series = Buffers(paddles, samples.Length);
            for (int i = 0; i < samples.Length; i++)
                for (int p = 0; p < paddles; p++) series[p][i] = samples[i].PaddleMm[p];
            Draw(BoardPlot, samples[0].TimeS, fs, series[..paddles], Enumerable.Range(1, paddles).Select(p => $"Paddle {p}").ToArray(), "Paddle Position (mm)");
        }
        else if (_vm.DaqStream)
        {
            var plots = new[] { Probe1, Probe2, Probe3 };
            int probes = samples[^1].ProbeM.Length;
            var series = Buffers(3, samples.Length);
            for (int k = 0; k < 3; k++)
            {
                int ch = Channels[k];
                if (ch >= probes) { Draw(plots[k], 0, fs, [], [], $"Ch {ch + 1}: not present"); continue; }
                for (int i = 0; i < samples.Length; i++) series[k][i] = samples[i].ProbeM[ch];
                Draw(plots[k], samples[0].TimeS, fs, [series[k]], [$"Probe {ch + 1}"], $"Probe {ch + 1} Elevation (m)", k);
            }
        }
        CountFrame();
    }

    private void DrawOffline(TestDataFile file)
    {
        double fs = file.SampleRateHz;
        double t0 = file.Column("t_s").FirstOrDefault();
        var paddles = file.Columns.Where(c => c.StartsWith("paddle", StringComparison.Ordinal)).ToArray();
        Draw(BoardPlot, t0, fs, paddles.Select(file.Column).ToArray(), paddles, "Paddle Position (mm)");
        var plots = new[] { Probe1, Probe2, Probe3 };
        for (int k = 0; k < 3; k++)
        {
            string col = $"probe{Channels[k] + 1}_m";
            if (file.Columns.Contains(col)) Draw(plots[k], t0, fs, [file.Column(col)], [col], $"Probe {Channels[k] + 1} Elevation (m)", k);
            else Draw(plots[k], 0, fs, [], [], $"{col}: not in file");
        }
    }

    private void Draw(WpfPlot plot, double t0, double fs, double[][] series, string[] names, string yLabel, int colourOffset = 0)
    {
        var p = plot.Plot;
        p.Clear();
        bool spectrum = Spectrum;
        PlotStyle.Apply(p, spectrum ? "Frequency (Hz)" : "Time (s)", spectrum ? $"PSD of {yLabel.Replace(" (mm)", "").Replace(" (m)", "")}" : yLabel);
        for (int k = 0; k < series.Length; k++)
        {
            var color = PlotStyle.Trace(k + colourOffset);
            if (spectrum && series[k].Length >= 64)
            {
                var s = WaveStatistics.Spectral(series[k], fs, Math.Min(series[k].Length, 4096));
                var sc = p.Add.Scatter(s.FrequencyHz, s.Psd, color);
                sc.MarkerSize = 0;
                sc.LineWidth = 1;
                sc.LegendText = names[k];
            }
            else if (!spectrum)
            {
                var sig = p.Add.Signal(series[k], 1 / fs, color);
                sig.Data.XOffset = t0;
                sig.LineWidth = PlotStyle.PaddleLineWidth(k, series.Length);
                sig.LegendText = names[k];
            }
        }
        if (series.Length > 1) p.ShowLegend(ScottPlot.Alignment.UpperLeft);
        p.Axes.AutoScale();
        if (spectrum) p.Axes.SetLimitsX(0, Math.Min(fs / 2, 5));
        plot.Refresh();
    }

    /// <summary>Reuses the arrays when the window is full, so steady-state frames allocate nothing.</summary>
    private double[][] Buffers(int count, int length)
    {
        if (_buffers.Length < count || _buffers.Any(b => b.Length != length))
            _buffers = Enumerable.Range(0, Math.Max(count, 8)).Select(_ => new double[length]).ToArray();
        return _buffers;
    }

    private void CountFrame()
    {
        _frames++;
        if (_fpsClock.ElapsedMilliseconds < 1000) return;
        FpsText.Text = $"{_frames * 1000.0 / _fpsClock.ElapsedMilliseconds:0} FPS";
        _frames = 0;
        _fpsClock.Restart();
    }
}
