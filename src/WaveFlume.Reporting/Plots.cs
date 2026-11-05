using ScottPlot;
using WaveFlume.Physics;

namespace WaveFlume.Reporting;

/// <summary>Off-screen ScottPlot renders for the report: white background, 1 px traces, labelled axes.</summary>
internal static class Plots
{
    private static readonly Color[] Palette =
    [
        Color.FromHex("#1F77B4"), Color.FromHex("#FF7F0E"), Color.FromHex("#2CA02C"),
        Color.FromHex("#9467BD"), Color.FromHex("#8C564B"), Color.FromHex("#17BECF"),
        Color.FromHex("#7F7F7F"), Color.FromHex("#BCBD22"),
    ];

    public static byte[] TimeSeries(double[] t, double[][] series, IReadOnlyList<string> names, string yLabel)
    {
        var p = new Plot();
        double fs = t.Length > 1 ? (t.Length - 1) / (t[^1] - t[0]) : 1;
        for (int k = 0; k < series.Length; k++)
        {
            var sig = p.Add.Signal(Array.ConvertAll(series[k], v => double.IsFinite(v) ? v : 0), 1 / fs, Palette[k % Palette.Length]);
            sig.Data.XOffset = t.Length > 0 ? t[0] : 0;
            sig.LineWidth = 1;
            sig.LegendText = names[k];
        }
        Style(p, "Time (s)", yLabel, series.Length > 1);
        return p.GetImageBytes(PdfReportService.PlotWidthPx, PdfReportService.PlotHeightPx, ImageFormat.Png);
    }

    public static byte[] Spectra(IReadOnlyList<(string Name, SpectralStats S)> spectra)
    {
        var p = new Plot();
        for (int k = 0; k < spectra.Count; k++)
        {
            var (name, s) = spectra[k];
            int n = s.FrequencyHz.TakeWhile(f => f <= 5).Count();
            var sc = p.Add.Scatter(s.FrequencyHz[..n], s.Psd[..n], Palette[k % Palette.Length]);
            sc.MarkerSize = 0;
            sc.LineWidth = 1;
            sc.LegendText = name;
        }
        Style(p, "Frequency (Hz)", "Spectral Density (m²/Hz)", spectra.Count > 1);
        return p.GetImageBytes(PdfReportService.PlotWidthPx, PdfReportService.PlotHeightPx, ImageFormat.Png);
    }

    private static void Style(Plot p, string x, string y, bool legend)
    {
        p.XLabel(x, 14);
        p.YLabel(y, 14);
        p.Grid.MajorLineColor = Color.FromHex("#E0E0E0");
        if (legend) p.ShowLegend(Alignment.UpperRight);
        p.Axes.AutoScale();
    }
}
