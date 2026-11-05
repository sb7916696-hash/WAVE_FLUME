using System.Text.Json;
using WaveFlume.Core;

namespace WaveFlume.Data;

/// <summary>One probe channel: η = Gain · mA + Offset, fitted from ≥ 3 known dip depths (spec §10).</summary>
public sealed record ProbeChannelCalibration(
    int Channel, double GainMPerMa, double OffsetM, double RSquared, DateTime CalibratedUtc,
    double[] RawMa, double[] KnownM)
{
    public double[] ResidualsM => RawMa.Select((x, i) => KnownM[i] - (GainMPerMa * x + OffsetM)).ToArray();
}

public static class ProbeCalibration
{
    public const int MinPoints = 3;

    /// <summary>4–20 mA loop fault thresholds (spec §8): open loop / sensor fault below, over-range above.</summary>
    public const double FaultLowMa = 3.8;
    public const double FaultHighMa = 20.5;

    public static bool IsLoopFault(double ma) => ma < FaultLowMa || ma > FaultHighMa;

    /// <summary>Ordinary least squares of known elevation on raw current.</summary>
    public static ProbeChannelCalibration Fit(int channel, IReadOnlyList<(double RawMa, double KnownM)> points, DateTime utcNow)
    {
        if (points.Count < MinPoints) throw new ArgumentException($"Need at least {MinPoints} dip points, have {points.Count}.");
        double mx = points.Average(p => p.RawMa), my = points.Average(p => p.KnownM);
        double sxx = points.Sum(p => (p.RawMa - mx) * (p.RawMa - mx));
        double sxy = points.Sum(p => (p.RawMa - mx) * (p.KnownM - my));
        double syy = points.Sum(p => (p.KnownM - my) * (p.KnownM - my));
        if (sxx < 1e-12) throw new ArgumentException("Raw readings do not vary: dip the probe to different depths.");
        double gain = sxy / sxx, offset = my - gain * mx;
        double rss = points.Sum(p => Math.Pow(p.KnownM - (gain * p.RawMa + offset), 2));
        double r2 = syy > 0 ? 1 - rss / syy : 1;
        return new ProbeChannelCalibration(channel, gain, offset, r2, utcNow,
            points.Select(p => p.RawMa).ToArray(), points.Select(p => p.KnownM).ToArray());
    }
}

/// <summary>Calibrations for all channels; channels without one use the nominal range from the config.</summary>
public sealed record CalibrationSet
{
    public List<ProbeChannelCalibration> Channels { get; init; } = [];

    public (double Gain, double Offset) For(int channel, ProbeSettings nominal) =>
        Channels.FirstOrDefault(c => c.Channel == channel) is { } c
            ? (c.GainMPerMa, c.OffsetM)
            : (nominal.NominalGainMPerMa, nominal.NominalOffsetM);

    public float[] Apply(float[] rawMa, ProbeSettings nominal)
    {
        var m = new float[rawMa.Length];
        for (int i = 0; i < rawMa.Length; i++)
        {
            var (g, o) = For(i, nominal);
            m[i] = (float)(g * rawMa[i] + o);
        }
        return m;
    }

    public CalibrationSet With(ProbeChannelCalibration c) =>
        new() { Channels = Channels.Where(x => x.Channel != c.Channel).Append(c).OrderBy(x => x.Channel).ToList() };

    public CalibrationSet Without(int channel) => new() { Channels = Channels.Where(x => x.Channel != channel).ToList() };

    public static CalibrationSet Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<CalibrationSet>(File.ReadAllText(path), FlumeConfig.JsonOptions) ?? new() : new();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, FlumeConfig.JsonOptions));
    }

    /// <summary>One-line summary for CSV headers and reports.</summary>
    public string Snapshot(ProbeSettings nominal, int channels) => string.Join("; ", Enumerable.Range(0, channels).Select(i =>
    {
        var (g, o) = For(i, nominal);
        var c = Channels.FirstOrDefault(x => x.Channel == i);
        return c is null
            ? FormattableString.Invariant($"ch{i + 1} nominal {g:0.######} m/mA {o:+0.####;-0.####} m")
            : FormattableString.Invariant($"ch{i + 1} {g:0.######} m/mA {o:+0.####;-0.####} m R2={c.RSquared:0.0000} ({c.CalibratedUtc:yyyy-MM-dd})");
    }));
}
