using System.Globalization;

namespace WaveFlume.Data;

/// <summary>Reads a run CSV written by <see cref="CsvLogger"/> (Open Test Data, final statistics, reports).</summary>
public sealed record TestDataFile(IReadOnlyDictionary<string, string> Header, IReadOnlyList<string> Columns, double[][] Data)
{
    public double[] Column(string name)
    {
        int i = Columns.ToList().IndexOf(name);
        return i >= 0 ? Data[i] : throw new KeyNotFoundException($"Column '{name}' not in file.");
    }

    public int ProbeCount => Columns.Count(c => c.StartsWith("probe", StringComparison.Ordinal));
    public int PaddleCount => Columns.Count(c => c.StartsWith("paddle", StringComparison.Ordinal));

    /// <summary>Sample rate from the median time step.</summary>
    public double SampleRateHz
    {
        get
        {
            var t = Column("t_s");
            if (t.Length < 2) return double.NaN;
            var dt = t.Zip(t.Skip(1), (a, b) => b - a).Order().ToList();
            return 1 / dt[dt.Count / 2];
        }
    }

    public static TestDataFile Load(string path)
    {
        var header = new Dictionary<string, string>();
        string[]? columns = null;
        var rows = new List<double[]>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                int colon = line.IndexOf(':');
                if (colon > 2) header[line[2..colon]] = line[(colon + 1)..].Trim();
                continue;
            }
            if (columns is null) { columns = line.Split(','); continue; }
            if (line.Length == 0) continue;
            var parts = line.Split(',');
            var row = new double[columns.Length];
            for (int i = 0; i < columns.Length; i++)
                row[i] = i < parts.Length && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
            rows.Add(row);
        }
        if (columns is null) throw new InvalidDataException($"{path} has no column header.");
        var data = new double[columns.Length][];
        for (int c = 0; c < columns.Length; c++)
        {
            data[c] = new double[rows.Count];
            for (int r = 0; r < rows.Count; r++) data[c][r] = rows[r][c];
        }
        return new TestDataFile(header, columns, data);
    }
}
