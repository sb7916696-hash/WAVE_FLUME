using FluentAssertions;
using WaveFlume.App.Services;
using WaveFlume.Plc;

namespace WaveFlume.App.Tests;

/// <summary>The installer's choices, applied on the program's first start, and the manual links.</summary>
public class InstallAndManualTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wf_inst_{Guid.NewGuid():N}");

    public InstallAndManualTests() => Directory.CreateDirectory(Path.Combine(_dir, "config"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Config => Path.Combine(_dir, "config");

    private SettingsStore Store(out string settingsPath)
    {
        settingsPath = Path.Combine(_dir, "user", "settings.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "flume_config.json"), Path.Combine(Config, "flume_config.json"));
        return new SettingsStore(settingsPath, Config);
    }

    [Fact]
    public void Without_an_installer_file_the_first_run_uses_the_simulator_and_the_default_data_folder()
    {
        var s = Store(out _).Load();
        s.Connection.Protocol.Should().Be(PlcProtocolType.Simulator);
        s.DataDirectory.Should().EndWith("WaveFlume");
    }

    [Fact]
    public void Installer_choices_are_applied_when_the_settings_are_first_created()
    {
        File.WriteAllText(Path.Combine(Config, InstallDefaults.FileName),
            """{ "Protocol": "OmronFins", "IpAddress": "192.168.250.10", "Port": 9600, "DataDirectory": "D:\\FlumeData" }""");
        var s = Store(out _).Load();
        s.Connection.Protocol.Should().Be(PlcProtocolType.OmronFins);
        s.Connection.IpAddress.Should().Be("192.168.250.10");
        s.Connection.Port.Should().Be(9600);
        s.Connection.TagMapProfile.Should().Be("omron_fins_dm.json");
        s.DataDirectory.Should().Be(@"D:\FlumeData");
    }

    [Theory]
    [InlineData("Simulator", PlcProtocolType.Simulator, "omron_fins_dm.json")]
    [InlineData("ModbusTcp", PlcProtocolType.ModbusTcp, "modbus_generic.json")]
    [InlineData("OpcUa", PlcProtocolType.OpcUa, "opcua_generic.json")]
    [InlineData("EtherNetIp", PlcProtocolType.EtherNetIp, "omron_eip_nx.json")]
    [InlineData("S7", PlcProtocolType.S7, "siemens_s7_db.json")]
    [InlineData("modbustcp", PlcProtocolType.ModbusTcp, "modbus_generic.json")]
    public void Each_controller_choice_selects_the_matching_tag_map(string protocol, PlcProtocolType expected, string profile)
    {
        File.WriteAllText(Path.Combine(Config, InstallDefaults.FileName), $$"""{ "Protocol": "{{protocol}}", "IpAddress": "10.0.0.5" }""");
        var s = Store(out _).Load();
        s.Connection.Protocol.Should().Be(expected);
        s.Connection.TagMapProfile.Should().Be(profile);
    }

    [Theory]
    [InlineData("""{ "Protocol": "Banana", "IpAddress": "10.0.0.5" }""")]
    [InlineData("""{ "Protocol": "ModbusTcp", "IpAddress": "not an address" }""")]
    [InlineData("""{ "DataDirectory": "relative\\folder" }""")]
    [InlineData("this is not json at all")]
    [InlineData("")]
    public void Invalid_installer_choices_are_ignored_and_never_stop_the_program_starting(string json)
    {
        File.WriteAllText(Path.Combine(Config, InstallDefaults.FileName), json);
        var s = Store(out _).Load();
        s.DataDirectory.Should().EndWith("WaveFlume", "a relative or missing folder is not used");
        s.Connection.IpAddress.Should().NotBe("not an address");
        s.Connection.Protocol.Should().BeOneOf(PlcProtocolType.Simulator, PlcProtocolType.ModbusTcp);
    }

    [Fact]
    public void Installer_choices_do_not_overwrite_settings_the_user_has_already_saved()
    {
        var store = Store(out _);
        var saved = store.Load() with { Theme = ThemeKind.Dark };
        store.Save(saved);
        File.WriteAllText(Path.Combine(Config, InstallDefaults.FileName), """{ "Protocol": "OpcUa", "IpAddress": "10.1.1.1" }""");
        var again = store.Load();
        again.Theme.Should().Be(ThemeKind.Dark);
        again.Connection.Protocol.Should().Be(PlcProtocolType.Simulator, "settings that already exist are left alone");
    }

    // ---- manual ---------------------------------------------------------------------------------

    [Fact]
    public void The_manual_pdf_is_found_in_the_docs_folder_and_absent_otherwise()
    {
        ManualLocator.FindPdf(_dir).Should().BeNull();
        Directory.CreateDirectory(Path.Combine(_dir, "docs"));
        File.WriteAllBytes(Path.Combine(_dir, "docs", ManualLocator.PdfName), [1, 2, 3]);
        ManualLocator.FindPdf(_dir).Should().EndWith(ManualLocator.PdfName);
    }

    [Theory]
    [InlineData("# comment only\n", null)]
    [InlineData("", null)]
    [InlineData("   \n\n", null)]
    [InlineData("https://drive.google.com/file/d/abc123/view?usp=sharing", "https://drive.google.com/file/d/abc123/view?usp=sharing")]
    [InlineData("# note\n\n  https://example.com/manual.pdf  \n", "https://example.com/manual.pdf")]
    [InlineData("http://example.com/m.pdf", "http://example.com/m.pdf")]
    [InlineData("ftp://example.com/m.pdf", null)]
    [InlineData("file:///C:/secret.pdf", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("not a link", null)]
    public void The_online_link_is_only_used_when_it_is_a_web_address(string content, string? expected)
    {
        File.WriteAllText(Path.Combine(Config, ManualLocator.LinkFileName), content);
        ManualLocator.ReadOnlineLink(Config).Should().Be(expected);
    }

    [Fact]
    public void A_missing_link_file_means_no_online_link() =>
        ManualLocator.ReadOnlineLink(Path.Combine(_dir, "nowhere")).Should().BeNull();

    [Fact]
    public void The_shipped_link_file_has_no_link_until_one_is_pasted_in()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "WaveFlume.sln"))) dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("WaveFlume.sln");
        File.Exists(Path.Combine(dir, "config", ManualLocator.LinkFileName)).Should().BeTrue();
    }
}
