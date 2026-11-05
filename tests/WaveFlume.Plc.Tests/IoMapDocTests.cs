using FluentAssertions;
using WaveFlume.Plc;

namespace WaveFlume.Plc.Tests;

public class IoMapDocTests
{
    /// <summary>docs/PLC_IO_MAP.md must match the shipped profiles. Set WAVEFLUME_REGEN_DOCS=1 to rewrite it.</summary>
    [Fact]
    public void Plc_io_map_doc_is_up_to_date()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "WaveFlume.sln"))) root = Path.GetDirectoryName(root)!;
        var profiles = Directory.GetFiles(Path.Combine(root, "config", "plc_profiles"), "*.json").Order().Select(TagMap.Load);
        string expected = PlcIoMap.ToMarkdown(profiles).ReplaceLineEndings("\n");
        string path = Path.Combine(root, "docs", "PLC_IO_MAP.md");

        if (Environment.GetEnvironmentVariable("WAVEFLUME_REGEN_DOCS") == "1")
            File.WriteAllText(path, expected);

        File.Exists(path).Should().BeTrue("run once with WAVEFLUME_REGEN_DOCS=1");
        File.ReadAllText(path).ReplaceLineEndings("\n").Should().Be(expected, "regenerate with WAVEFLUME_REGEN_DOCS=1");
    }
}
