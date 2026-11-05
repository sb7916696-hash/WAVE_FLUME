using System.Text;
using System.Threading.Channels;
using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Data;
using WaveFlume.Reporting;

namespace WaveFlume.Data.Tests;

public class CalibrationAndReportTests
{
    [Fact]
    public void Least_squares_fit_recovers_line_and_reports_residuals()
    {
        // η = 0.03125·mA − 0.375 (4–20 mA ↔ ±0.25 m) with ±1 mm scatter.
        var pts = new (double, double)[] { (4.0, -0.249), (8.0, -0.124), (12.0, 0.001), (16.0, 0.124), (20.0, 0.251) };
        var c = ProbeCalibration.Fit(0, pts, DateTime.UtcNow);
        c.GainMPerMa.Should().BeApproximately(0.03125, 2e-4);
        c.OffsetM.Should().BeApproximately(-0.375, 3e-3);
        c.RSquared.Should().BeGreaterThan(0.9999);
        c.ResidualsM.Should().HaveCount(5).And.OnlyContain(r => Math.Abs(r) < 0.002);
    }

    [Fact]
    public void Fit_needs_three_distinct_points()
    {
        FluentActions.Invoking(() => ProbeCalibration.Fit(0, [(4, 0), (5, 1)], DateTime.UtcNow)).Should().Throw<ArgumentException>().WithMessage("*3*");
        FluentActions.Invoking(() => ProbeCalibration.Fit(0, [(5, 0), (5, 1), (5, 2)], DateTime.UtcNow)).Should().Throw<ArgumentException>().WithMessage("*vary*");
    }

    [Theory]
    [InlineData(3.79, true)]
    [InlineData(3.8, false)]
    [InlineData(20.5, false)]
    [InlineData(20.51, true)]
    [InlineData(0, true)]
    public void Loop_fault_thresholds(double ma, bool fault) => ProbeCalibration.IsLoopFault(ma).Should().Be(fault);

    [Fact]
    public void Calibration_set_uses_nominal_until_calibrated_and_persists()
    {
        var nominal = new ProbeSettings();
        var set = new CalibrationSet();
        set.Apply([4f, 12f, 20f], nominal).Should().Equal(-0.25f, 0f, 0.25f);

        set = set.With(ProbeCalibration.Fit(1, [(4, 0), (12, 0.1), (20, 0.2)], DateTime.UtcNow));
        var m = set.Apply([4f, 12f, 20f], nominal);
        m[0].Should().Be(-0.25f);
        m[1].Should().BeApproximately(0.1f, 1e-6f);

        var path = Path.Combine(Path.GetTempPath(), $"wf_cal_{Guid.NewGuid():N}.json");
        set.Save(path);
        CalibrationSet.Load(path).Channels.Should().ContainSingle().Which.Channel.Should().Be(1);
        set.Without(1).Channels.Should().BeEmpty();
        set.Snapshot(nominal, 2).Should().Contain("ch1 nominal").And.Contain("ch2 0.0125");
        File.Delete(path);
    }

    [Fact]
    public async Task Pdf_report_from_a_run_csv()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wf_rep_{Guid.NewGuid():N}");
        var csv = Path.Combine(dir, "run.csv");
        var ch = Channel.CreateUnbounded<TelemetrySnapshot>();
        var writer = CsvLogger.WriteAsync(ch.Reader, csv, new Dictionary<string, string> { ["test_number"] = "T-0001" }, 6, 3, default);
        for (int i = 0; i < 125 * 40; i++)
        {
            double t = i / 125.0, x = 13.6 * Math.Sin(2 * Math.PI * t);
            await ch.Writer.WriteAsync(new TelemetrySnapshot(t, i, Enumerable.Repeat((float)x, 6).ToArray(),
                [(float)(0.025 * Math.Cos(2 * Math.PI * t)), (float)(0.02 * Math.Cos(2 * Math.PI * (t - 5))), 0f], 0, 0, 0));
        }
        ch.Writer.Complete();
        await writer;

        var pdf = Path.Combine(dir, "run.pdf");
        var progress = new List<double>();
        PdfReportService.Generate(new ReportInput
        {
            CsvPath = csv, TestNumber = "T-0001", StartUtc = DateTime.UtcNow,
            Flume = new FlumeConfig { Limits = new OperationalLimits { MaxAccelMs2 = 5 } },
            Request = new WaveRequest { NumWaves = 40, RampS = 2 },
            Alerts = [new Alert(DateTime.UtcNow, AlertSeverity.Warning, AlertSources.WaveProbe, "Test alert")],
            CalibrationSnapshot = "ch1 nominal",
            SkipStartS = 2, SkipEndS = 2,
        }, pdf, new Progress<double>(progress.Add));

        var bytes = await File.ReadAllBytesAsync(pdf);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
        bytes.Length.Should().BeGreaterThan(30_000, "three embedded plots");
        Directory.Delete(dir, true);
    }
}
