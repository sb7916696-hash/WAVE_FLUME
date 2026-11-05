using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using WaveFlume.Core;
using WaveFlume.Data;
using WaveFlume.Physics;

namespace WaveFlume.Reporting;

public sealed record ReportInput
{
    public required string CsvPath { get; init; }
    public required string TestNumber { get; init; }
    public required FlumeConfig Flume { get; init; }
    public string ProjectTitle { get; init; } = "Wave Flume - 6-Paddle Segmented Wavemaker";
    public DateTime StartUtc { get; init; }
    public WaveRequest? Request { get; init; }
    public IReadOnlyList<Alert> Alerts { get; init; } = [];
    public string CalibrationSnapshot { get; init; } = "";
    public byte[]? LogoPng { get; init; }

    /// <summary>Ramp segments excluded from the statistics.</summary>
    public double SkipStartS { get; init; }
    public double SkipEndS { get; init; }
}

/// <summary>
/// Multi-page A4 run report (spec §13): header, recipe, calculated kinematics, limit utilisation,
/// time-series and spectrum plots, statistics, alerts, page-numbered footer.
/// QuestPDF Community licence — see the README for eligibility.
/// </summary>
public static class PdfReportService
{
    public const int PlotWidthPx = 1000;
    public const int PlotHeightPx = 330;

    static PdfReportService() => QuestPDF.Settings.License = LicenseType.Community;

    public static void Generate(ReportInput input, string pdfPath, IProgress<double>? progress = null)
    {
        var inv = CultureInfo.InvariantCulture;
        var data = TestDataFile.Load(input.CsvPath);
        progress?.Report(0.1);
        double fs = data.SampleRateHz;
        var t = data.Column("t_s");
        var probes = data.Columns.Where(c => c.StartsWith("probe", StringComparison.Ordinal)).ToList();
        var paddles = data.Columns.Where(c => c.StartsWith("paddle", StringComparison.Ordinal)).ToList();

        WaveResult? calc = null;
        string? calcError = null;
        if (input.Request is { } req)
        {
            try { calc = new WaveMathEngine(input.Flume).Calculate(req); }
            catch (Exception ex) when (ex is NotImplementedException or ArgumentException or InvalidOperationException or IOException)
            {
                calcError = ex.Message;
            }
        }
        progress?.Report(0.3);

        var stats = probes.Select(p => (Name: p, Z: Stats(data.Column(p), fs, input), S: Spectrum(data.Column(p), fs))).ToList();
        byte[] paddlePng = Plots.TimeSeries(t, paddles.Select(data.Column).ToArray(), paddles, "Paddle Position (mm)");
        byte[] etaPng = Plots.TimeSeries(t, probes.Select(data.Column).ToArray(), probes, "Measured Elevation (m)");
        byte[] specPng = Plots.Spectra(stats.Where(s => s.S is not null).Select(s => (s.Name, s.S!)).ToList());
        progress?.Report(0.6);

        var l = input.Flume.Limits;
        double clampMm = Units.MToMm(Math.Min(l.MaxStrokeM, HardLimits.MaxStrokeCeilingM));
        double measuredMm = paddles.Count == 0 ? 0 : paddles.Max(p => data.Column(p).Where(double.IsFinite).Select(Math.Abs).DefaultIfEmpty().Max());

        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(15, Unit.Millimetre);
            page.DefaultTextStyle(x => x.FontSize(9));

            page.Header().BorderBottom(1).PaddingBottom(4).Row(row =>
            {
                if (input.LogoPng is { } logo) row.ConstantItem(40).Height(30).Image(logo).FitArea();
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(input.ProjectTitle).FontSize(13).SemiBold();
                    c.Item().Text($"Test Report - Test {input.TestNumber}");
                });
                row.ConstantItem(170).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text($"Run start: {input.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
                    c.Item().AlignRight().Text($"Data: {Path.GetFileName(input.CsvPath)}");
                });
            });

            page.Content().PaddingVertical(6).Column(col =>
            {
                col.Spacing(8);

                Section(col, "Input recipe");
                if (input.Request is { } q)
                {
                    KeyValues(col,
                    [
                        ("Wave type", $"{q.Type} ({(q.Order == WaveOrder.First ? "1st" : "2nd")} order)"),
                        ("Water depth", F(q.WaterDepthM, "0.000", "m")),
                        (q.Type == WaveType.Random ? "Hs" : "Wave height", F(q.WaveHeightM, "0.0000", "m")),
                        (q.Type == WaveType.Random ? "Tp" : "Period", F(q.PeriodS, "0.000", "s")),
                        ("Number of waves", q.NumWaves.ToString(inv)),
                        ("Sampling frequency", F(q.SamplingHz, "0", "Hz")),
                        ("% signal modification", F(q.SignalModPct, "0", "%")),
                        ("Ramp duration", F(q.RampS, "0.0", "s")),
                        ("Wave angle", F(q.AngleDeg, "0.0", "deg")),
                        ("Spectrum / seed", q.Type is WaveType.Random or WaveType.Focusing ? $"{q.Spectrum}, gamma {q.Gamma}, seed {q.RandomSeed}" : "-"),
                    ]);
                }
                else col.Item().Text("Recipe not recorded.");

                Section(col, "Calculated kinematics");
                if (calc is not null)
                {
                    var k = calc.Kinematics;
                    var w = calc.Wavemaker;
                    KeyValues(col,
                    [
                        ("Stroke amplitude / peak-to-peak", $"{w.StrokeAmplitudeM.ToString("0.00000", inv)} / {w.StrokePeakToPeakM.ToString("0.00000", inv)} m"),
                        ("Peak velocity", F(w.PeakVelocityMs, "0.0000", "m/s")),
                        ("Peak acceleration", F(w.PeakAccelMs2, "0.0000", "m/s²")),
                        ("Force (estimate)", w.ForceEstimateN is { } fN ? F(fN / 1000, "0.0000", "kN") : "-"),
                        ("Power", w.PowerW is { } p ? F(p, "0.00", "W") : "-"),
                        ("Wave length / celerity", k is null ? "-" : $"{k.WaveLengthM.ToString("0.000", inv)} m / {k.CelerityMs.ToString("0.000", inv)} m/s"),
                        ("k, kh, regime", k is null ? "-" : $"{k.K.ToString("0.000", inv)} rad/m, {k.Kh.ToString("0.000", inv)}, {k.Regime}"),
                    ]);
                    foreach (var line in calc.Info) col.Item().Text(line).FontSize(8).Italic();
                }
                else col.Item().Text(calcError is null ? "Not available." : $"Not recalculated: {calcError}");

                Section(col, "Limit utilisation");
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
                    Header(table, "Quantity", "Value", "Limit", "Utilisation");
                    Util(table, "Commanded stroke (mm)", calc is null ? null : Units.MToMm(calc.Safety.Worst.MaxAbsPositionM), clampMm);
                    Util(table, "Measured stroke (mm)", measuredMm, clampMm);
                    Util(table, "Commanded velocity (m/s)", calc?.Safety.Worst.MaxAbsVelocityMs, Math.Min(l.MaxVelocityMs, HardLimits.MaxVelocityCeilingMs));
                    Util(table, "Commanded acceleration (m/s²)", calc?.Safety.Worst.MaxAbsAccelMs2, l.MaxAccelMs2);
                });

                Section(col, "Paddle motion");
                col.Item().Image(paddlePng).FitWidth();
                Section(col, "Wave elevation");
                col.Item().Image(etaPng).FitWidth();
                Section(col, "Measured spectrum (Welch, Hann)");
                col.Item().Image(specPng).FitWidth();

                Section(col, "Wave statistics (zero up-crossing; ramps excluded)");
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { for (int i = 0; i < 10; i++) c.RelativeColumn(); });
                    Header(table, "Probe", "Waves", "H1/3 m", "H10 m", "Hmax m", "T1/3 s", "T10 s", "Tmax s", "Hm0 m", "Tp s");
                    foreach (var (name, z, s) in stats)
                    {
                        Cell(table, name);
                        Cell(table, z.WaveCount.ToString(inv));
                        foreach (var v in new[] { z.H13M, z.H10M, z.HmaxM }) Cell(table, Num(v, "0.0000"));
                        foreach (var v in new[] { z.T13S, z.T10S, z.TmaxS }) Cell(table, Num(v, "0.000"));
                        Cell(table, s is null ? "-" : Num(s.Hm0M, "0.0000"));
                        Cell(table, s is null ? "-" : Num(s.TpS, "0.000"));
                    }
                });
                if (input.CalibrationSnapshot.Length > 0)
                    col.Item().Text($"Probe calibration: {input.CalibrationSnapshot}").FontSize(8);

                Section(col, $"Alerts ({input.Alerts.Count})");
                if (input.Alerts.Count == 0) col.Item().Text("None.");
                else
                {
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.ConstantColumn(110); c.ConstantColumn(55); c.ConstantColumn(80); c.RelativeColumn(); });
                        Header(table, "Time", "Severity", "Source", "Description");
                        foreach (var a in input.Alerts)
                        {
                            Cell(table, a.UtcTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv));
                            Cell(table, a.Severity.ToString());
                            Cell(table, a.Source);
                            Cell(table, a.Description);
                        }
                    });
                }
            });

            page.Footer().BorderTop(1).PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by Wave Flume").FontSize(7);
                row.RelativeItem().AlignRight().Text(x =>
                {
                    x.Span("Page ").FontSize(8);
                    x.CurrentPageNumber().FontSize(8);
                    x.Span(" of ").FontSize(8);
                    x.TotalPages().FontSize(8);
                });
            });
        })).GeneratePdf(pdfPath);
        progress?.Report(1);
    }

    private static ZeroCrossingStats Stats(double[] eta, double fs, ReportInput input)
    {
        var clean = Array.ConvertAll(eta, v => double.IsFinite(v) ? v : 0);
        return WaveStatistics.ZeroUpcrossing(clean, fs, input.SkipStartS, input.SkipEndS);
    }

    private static SpectralStats? Spectrum(double[] eta, double fs)
    {
        var clean = Array.ConvertAll(eta, v => double.IsFinite(v) ? v : 0);
        return clean.Length >= 64 ? WaveStatistics.Spectral(clean, fs, Math.Min(clean.Length, 4096)) : null;
    }

    private static string F(double v, string fmt, string unit) => $"{v.ToString(fmt, CultureInfo.InvariantCulture)} {unit}";
    private static string Num(double v, string fmt) => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";

    private static void Section(ColumnDescriptor col, string title) =>
        col.Item().PaddingTop(4).BorderBottom(0.5f).Text(title).FontSize(11).SemiBold();

    private static void KeyValues(ColumnDescriptor col, (string Key, string Value)[] rows) => col.Item().Table(table =>
    {
        table.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(2); });
        foreach (var (k, v) in rows)
        {
            Cell(table, k);
            Cell(table, v);
        }
    });

    private static void Header(TableDescriptor table, params string[] titles)
    {
        foreach (var h in titles) table.Cell().Background(Colors.Grey.Lighten3).Border(0.5f).Padding(2).Text(h).SemiBold();
    }

    private static void Cell(TableDescriptor table, string text) => table.Cell().Border(0.5f).Padding(2).Text(text);

    private static void Util(TableDescriptor table, string name, double? value, double? limit)
    {
        Cell(table, name);
        Cell(table, value is { } v ? v.ToString("0.000", CultureInfo.InvariantCulture) : "-");
        Cell(table, limit is { } m ? m.ToString("0.000", CultureInfo.InvariantCulture) : "not configured");
        Cell(table, value is { } a && limit is { } b && b > 0 ? $"{100 * a / b:0.0} %" : "-");
    }
}
