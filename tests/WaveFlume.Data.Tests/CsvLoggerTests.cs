using System.Diagnostics;
using System.Threading.Channels;
using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Data;

namespace WaveFlume.Data.Tests;

public class CsvLoggerTests
{
    private static TelemetrySnapshot Sample(long i) =>
        new(i / 200.0, i, [1.5f, -2.25f, 0, 0, 0, (float)i], [0.01f, -0.02f, 0.03f, 0.04f, 0, 0, 0, 0.123f], 5, 0, (ushort)i);

    [Fact]
    public void Run_file_name_is_timestamped_and_safe()
    {
        CsvLogger.RunFileName(new DateTime(2026, 10, 30, 13, 5, 9, DateTimeKind.Utc), "T-01/a")
            .Should().Be("20261030_130509_T-01_a.csv");
    }

    [Fact]
    public async Task Writes_header_columns_and_all_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf_{Guid.NewGuid():N}", "run.csv");
        var ch = Channel.CreateUnbounded<TelemetrySnapshot>();
        var writer = CsvLogger.WriteAsync(ch.Reader, path,
            new Dictionary<string, string> { ["test_number"] = "T-01", ["units"] = "paddle mm, probe m" }, 6, 8, CancellationToken.None);
        for (long i = 0; i < 1000; i++) await ch.Writer.WriteAsync(Sample(i));
        ch.Writer.Complete();
        (await writer).Should().Be(1000);

        var lines = await File.ReadAllLinesAsync(path);
        lines[0].Should().Be("# test_number: T-01");
        lines[2].Should().Be("t_s,paddle1_mm,paddle2_mm,paddle3_mm,paddle4_mm,paddle5_mm,paddle6_mm," +
                             "probe1_m,probe2_m,probe3_m,probe4_m,probe5_m,probe6_m,probe7_m,probe8_m,status_word,sample_counter");
        lines.Should().HaveCount(3 + 1000);
        lines[3 + 999].Should().Be("4.9950,1.500,-2.250,0.000,0.000,0.000,999.000,0.01000,-0.02000,0.03000,0.04000,0.00000,0.00000,0.00000,0.12300,5,999");
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public async Task Sustains_far_more_than_200_hz_by_20_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wf_{Guid.NewGuid():N}.csv");
        var ch = Channel.CreateBounded<TelemetrySnapshot>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });
        var sw = Stopwatch.StartNew();
        var writer = CsvLogger.WriteAsync(ch.Reader, path, new Dictionary<string, string>(), 6, 8, CancellationToken.None);
        const int n = 200 * 600; // ten minutes of 200 Hz data
        for (long i = 0; i < n; i++) await ch.Writer.WriteAsync(Sample(i));
        ch.Writer.Complete();
        (await writer).Should().Be(n);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "must be far faster than real time (600 s)");
        File.Delete(path);
    }
}
