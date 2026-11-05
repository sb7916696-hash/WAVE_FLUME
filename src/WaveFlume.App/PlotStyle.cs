using System.Windows;
using ScottPlot;
using WpfColor = System.Windows.Media.Color;

namespace WaveFlume.App;

/// <summary>
/// Plain plots (spec §11.0): theme background, 1 px solid traces in a fixed palette, light grid,
/// labelled axes with units. Oscilloscope option: black background, yellow trace.
/// </summary>
public static class PlotStyle
{
    /// <summary>Fixed trace palette. Red is reserved for alarms and never used for data.</summary>
    public static readonly Color[] Palette =
    [
        Color.FromHex("#1F77B4"), Color.FromHex("#FF7F0E"), Color.FromHex("#2CA02C"),
        Color.FromHex("#9467BD"), Color.FromHex("#8C564B"), Color.FromHex("#17BECF"),
        Color.FromHex("#7F7F7F"), Color.FromHex("#BCBD22"),
    ];

    public static readonly Color ScopeTrace = Color.FromHex("#FFD800");

    public static bool Oscilloscope { get; set; }

    public static Color Trace(int i) => Oscilloscope ? ScopeTrace : Palette[i % Palette.Length];

    /// <summary>
    /// Line width for one of several overlapping traces: the first is the thickest and the last the thinnest, so a
    /// trace drawn on top of another never hides its colour completely. One trace, or oscilloscope style, is 1.
    /// </summary>
    public static float PaddleLineWidth(int index, int count) =>
        Oscilloscope || count <= 1 ? 1 : 1 + (count - 1 - index) * 0.8f;

    public static void Apply(Plot plot, string xLabel, string yLabel)
    {
        Color bg = Oscilloscope ? Color.FromHex("#000000") : FromResource("PlotBgColor", Colors.White);
        Color fg = Oscilloscope ? Color.FromHex("#D0D0D0") : FromResource("PlotFgColor", Colors.Black);
        Color grid = Oscilloscope ? Color.FromHex("#303030") : FromResource("PlotGridColor", Colors.LightGray);
        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color = bg;
        plot.Axes.Color(fg);
        plot.Grid.MajorLineColor = grid;
        plot.Grid.MinorLineColor = Colors.Transparent;
        plot.XLabel(xLabel, 12);
        plot.YLabel(yLabel, 12);
        plot.Legend.BackgroundColor = bg;
        plot.Legend.FontColor = fg;
        plot.Legend.OutlineColor = grid;
    }

    private static Color FromResource(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is WpfColor c ? new Color(c.R, c.G, c.B, c.A) : fallback;
}
