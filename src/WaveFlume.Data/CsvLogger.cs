using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using WaveFlume.Core;

namespace WaveFlume.Data;

/// <summary>
/// One CSV per run (spec §10): <c>t_s, paddle1..N_mm, probe1..M_m, status_word, sample_counter</c>,
/// preceded by <c># key: value</c> header lines (recipe, units, calibration snapshot).
/// Consumes the telemetry channel until it completes; flushes every second and on stop.
/// </summary>
public static class CsvLogger
{
    public const double FlushIntervalS = 1.0;

    public static string RunFileName(DateTime utcStart, string testNumber)
    {
        var safe = string.Concat(testNumber.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return $"{utcStart:yyyyMMdd_HHmmss}_{safe}.csv";
    }

    public static async Task<long> WriteAsync(
        ChannelReader<TelemetrySnapshot> samples,
        string path,
        IReadOnlyDictionary<string, string> header,
        int paddleCount,
        int probeCount,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16);
        foreach (var (key, value) in header)
            await w.WriteLineAsync($"# {key}: {value}".AsMemory(), ct).ConfigureAwait(false);
        await w.WriteLineAsync(string.Join(",",
            new[] { "t_s" }
                .Concat(Enumerable.Range(1, paddleCount).Select(i => $"paddle{i}_mm"))
                .Concat(Enumerable.Range(1, probeCount).Select(i => $"probe{i}_m"))
                .Concat(["status_word", "sample_counter"])).AsMemory(), ct).ConfigureAwait(false);

        var sb = new StringBuilder(256);
        var inv = CultureInfo.InvariantCulture;
        var flush = Stopwatch.StartNew();
        long rows = 0;
        try
        {
            await foreach (var s in samples.ReadAllAsync(ct).ConfigureAwait(false))
            {
                sb.Clear().Append(s.TimeS.ToString("F4", inv));
                for (int i = 0; i < paddleCount; i++) sb.Append(',').Append(i < s.PaddleMm.Length ? s.PaddleMm[i].ToString("F3", inv) : "");
                for (int i = 0; i < probeCount; i++) sb.Append(',').Append(i < s.ProbeM.Length ? s.ProbeM[i].ToString("F5", inv) : "");
                sb.Append(',').Append(s.StatusWord).Append(',').Append(s.SampleCounter);
                await w.WriteLineAsync(sb, ct).ConfigureAwait(false);
                rows++;
                if (flush.Elapsed.TotalSeconds >= FlushIntervalS)
                {
                    await w.FlushAsync(ct).ConfigureAwait(false);
                    flush.Restart();
                }
            }
        }
        finally
        {
            await w.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return rows;
    }
}
