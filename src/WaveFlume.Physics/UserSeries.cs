using System.Globalization;

namespace WaveFlume.Physics;

/// <summary>
/// User-defined input (spec §7.5): CSV "time_s, value[, value…]" in metres. Lines that do not parse
/// (headers, comments) are skipped; separators may be comma, semicolon, tab or space.
/// </summary>
public sealed record UserSeries(double[] TimesS, double[][] Columns)
{
    public static UserSeries Load(string path)
    {
        var times = new List<double>();
        List<double>[]? cols = null;
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split([',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var values = new double[parts.Length];
            bool ok = true;
            for (int i = 0; i < parts.Length && ok; i++)
                ok = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]);
            if (!ok) continue;
            cols ??= Enumerable.Range(0, parts.Length - 1).Select(_ => new List<double>()).ToArray();
            if (values.Length - 1 != cols.Length)
                throw new InvalidDataException($"{path}: inconsistent column count at t = {values[0]}.");
            if (times.Count > 0 && values[0] <= times[^1])
                throw new InvalidDataException($"{path}: time must increase (t = {values[0]}).");
            times.Add(values[0]);
            for (int c = 0; c < cols.Length; c++) cols[c].Add(values[c + 1]);
        }
        if (cols is null || times.Count < 2) throw new InvalidDataException($"{path}: needs at least two numeric rows.");
        return new UserSeries(times.ToArray(), cols.Select(c => c.ToArray()).ToArray());
    }

    public double DurationS => TimesS[^1] - TimesS[0];

    /// <summary>Linear interpolation onto t = 0, 1/fs, … covering the series.</summary>
    public double[] Resample(int column, double fs)
    {
        var src = Columns[column];
        int n = (int)Math.Floor(DurationS * fs) + 1;
        var y = new double[n];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            double t = TimesS[0] + i / fs;
            while (k < TimesS.Length - 2 && TimesS[k + 1] < t) k++;
            double u = (t - TimesS[k]) / (TimesS[k + 1] - TimesS[k]);
            y[i] = src[k] + Math.Clamp(u, 0, 1) * (src[k + 1] - src[k]);
        }
        return y;
    }
}
