using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaveFlume.Plc;

public enum TagDataType { Bool, UInt16, Int32, Float32 }

public enum TagAccess { R, W, RW }

/// <summary>Logical signals the application knows (spec §9.1). Arrays use <see cref="TagEntry.Length"/>.</summary>
public static class Signals
{
    public const string CmdWord = "CmdWord";
    public const string HeartbeatPc = "HeartbeatPc";
    public const string PeriodS = "Period_s";
    public const string RampS = "Ramp_s";
    public const string Cycles = "Cycles";
    public const string AmpMm = "Amp_mm";
    public const string PhaseDeg = "Phase_deg";
    public const string PosMm = "Pos_mm";
    public const string ProbeM = "Probe_m";
    public const string StatusWord = "StatusWord";
    public const string FaultWord = "FaultWord";
    public const string Heartbeat = "Heartbeat";
    public const string SampleCounter = "SampleCounter";
    public const string TrajChunk = "TrajChunk";
    public const string ChunkSeq = "ChunkSeq";
    public const string ChunkReady = "ChunkReady";
    public const string ChunkCrc = "ChunkCrc";
    public const string ChunkAck = "ChunkAck";
    public const string BufferLevel = "BufferLevel";

    public const int MaxProbes = 8;

    /// <summary>Signal → (direction the PC needs, min length, max length).</summary>
    public static readonly IReadOnlyDictionary<string, (bool PcWrites, int MinLength, int MaxLength)> Required =
        new Dictionary<string, (bool, int, int)>
        {
            [CmdWord] = (true, 1, 1),
            [HeartbeatPc] = (true, 1, 1),
            [PeriodS] = (true, 1, 1),
            [RampS] = (true, 1, 1),
            [Cycles] = (true, 1, 1),
            [AmpMm] = (true, 1, 64),
            [PhaseDeg] = (true, 1, 64),
            [PosMm] = (false, 1, 64),
            [ProbeM] = (false, 1, MaxProbes),
            [StatusWord] = (false, 1, 1),
            [FaultWord] = (false, 1, 1),
            [Heartbeat] = (false, 1, 1),
            [SampleCounter] = (false, 1, 1),
            [TrajChunk] = (true, 1, 4096),
            [ChunkSeq] = (true, 1, 1),
            [ChunkReady] = (true, 1, 1),
            [ChunkCrc] = (true, 1, 1),
            [ChunkAck] = (false, 1, 1),
            [BufferLevel] = (false, 1, 1),
        };
}

/// <summary>One row of the TagMap editor: logical signal → protocol address.</summary>
public sealed record TagEntry
{
    public required string Signal { get; init; }
    public required string Address { get; init; }
    public TagDataType DataType { get; init; } = TagDataType.UInt16;
    public int Length { get; init; } = 1;

    /// <summary>engineering = raw · Scale + Offset.</summary>
    public double Scale { get; init; } = 1.0;
    public double Offset { get; init; }
    public TagAccess Access { get; init; } = TagAccess.R;
    public string Units { get; init; } = "";

    public double ToEngineering(double raw) => raw * Scale + Offset;
    public double ToRaw(double engineering) => (engineering - Offset) / Scale;
}

/// <summary>Named profile ("Omron NX102 – lab") mapping every logical signal for one protocol.</summary>
public sealed record TagMap
{
    public required string Name { get; init; }
    public PlcProtocolType Protocol { get; init; }
    public WordOrder WordOrder { get; init; } = WordOrder.ABCD;
    public List<TagEntry> Tags { get; init; } = [];

    public TagEntry Get(string signal) =>
        Tags.FirstOrDefault(t => t.Signal == signal)
        ?? throw new KeyNotFoundException($"TagMap '{Name}' has no entry for signal '{signal}'.");

    public int PaddleCount => Get(Signals.PosMm).Length;

    /// <param name="checkAddress">Driver-specific address check; returns an error or null.</param>
    public IReadOnlyList<string> Validate(Func<TagEntry, string?> checkAddress)
    {
        var errors = new List<string>();
        foreach (var dup in Tags.GroupBy(t => t.Signal).Where(g => g.Count() > 1))
            errors.Add($"Signal '{dup.Key}' is mapped more than once.");

        foreach (var (signal, (pcWrites, min, max)) in Signals.Required)
        {
            var tag = Tags.FirstOrDefault(t => t.Signal == signal);
            if (tag is null) { errors.Add($"Signal '{signal}' is not mapped."); continue; }
            if (tag.Length < min || tag.Length > max)
                errors.Add($"Signal '{signal}' length {tag.Length} outside [{min}, {max}].");
            if (pcWrites ? tag.Access == TagAccess.R : tag.Access == TagAccess.W)
                errors.Add($"Signal '{signal}' needs access {(pcWrites ? "W" : "R")}.");
            if (tag.Scale == 0)
                errors.Add($"Signal '{signal}' has zero scale.");
            if (checkAddress(tag) is { } err)
                errors.Add($"Signal '{signal}': {err}");
        }
        foreach (var unknown in Tags.Where(t => !Signals.Required.ContainsKey(t.Signal)))
            errors.Add($"Unknown signal '{unknown.Signal}'.");

        if (errors.Count == 0)
        {
            int paddles = PaddleCount;
            if (Get(Signals.AmpMm).Length != paddles || Get(Signals.PhaseDeg).Length != paddles)
                errors.Add("Amp_mm and Phase_deg must have one element per paddle (Pos_mm length).");
            if (Get(Signals.TrajChunk).Length % paddles != 0)
                errors.Add("TrajChunk length must be a multiple of the paddle count.");
        }
        return errors;
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TagMap Load(string path) =>
        JsonSerializer.Deserialize<TagMap>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException($"{path} is empty.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
