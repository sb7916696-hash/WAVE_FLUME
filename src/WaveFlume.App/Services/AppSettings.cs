using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.App.Services;

public enum ThemeKind { Light, Dark }

public enum DaqSourceKind { PlcAnalog, Simulator, ExternalDaq }

public sealed record ConnectionSettings
{
    public PlcProtocolType Protocol { get; init; } = PlcProtocolType.Simulator;
    public string IpAddress { get; init; } = "192.168.250.1";
    public int Port { get; init; }
    public PlcTransport Transport { get; init; } = PlcTransport.Udp;
    public int TimeoutMs { get; init; } = 1000;
    public int Retries { get; init; } = 3;
    public Dictionary<string, string> Extras { get; init; } = [];

    /// <summary>DPAPI (CurrentUser) protected, base64. Never stored in clear text.</summary>
    public string? ProtectedOpcUaPassword { get; init; }
    public string TagMapProfile { get; init; } = "omron_fins_dm.json";
    public DaqSourceKind DaqSource { get; init; } = DaqSourceKind.PlcAnalog;
    public string? ExternalDaqIp { get; init; } = "192.168.169.5";

    public PlcEndpoint ToEndpoint()
    {
        var extras = new Dictionary<string, string>(Extras);
        if (Secrets.Unprotect(ProtectedOpcUaPassword) is { } pw) extras[EndpointExtras.OpcUaPassword] = pw;
        return new PlcEndpoint
        {
            IpAddress = IpAddress,
            Port = Port > 0 ? Port : DriverFactory.DefaultPort(Protocol),
            Transport = Transport,
            TimeoutMs = TimeoutMs,
            Retries = Retries,
            Extras = extras,
        };
    }
}

public sealed record AppSettings
{
    public FlumeConfig Flume { get; init; } = new();
    public ConnectionSettings Connection { get; init; } = new();
    public ThemeKind Theme { get; init; } = ThemeKind.Light;
    public bool OscilloscopePlots { get; init; }
    public string DataDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WaveFlume");
    public double WaveBoardLocationM { get; init; }
    public double CalibrationFactor { get; init; } = 1.0;
    public double ExtraRecordS { get; init; } = 1.0;

    public string RunsDirectory => Path.Combine(DataDirectory, "Runs");
}

public static class Secrets
{
    public static string? Protect(string? plain) => string.IsNullOrEmpty(plain)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; } // other user/machine, or corrupted
    }

}

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON under %LOCALAPPDATA%\WaveFlume.</summary>
public sealed class SettingsStore
{
    private readonly string path;
    private readonly string shippedConfigDirectory;

    /// <summary>Profiles live next to the settings file (writable), seeded once from the shipped copies.</summary>
    public SettingsStore(string path, string shippedConfigDirectory)
    {
        this.path = path;
        this.shippedConfigDirectory = shippedConfigDirectory;
        ProfilesDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "plc_profiles");
        var shipped = Path.Combine(shippedConfigDirectory, "plc_profiles");
        if (!Directory.Exists(shipped)) return;
        Directory.CreateDirectory(ProfilesDirectory);
        foreach (var file in Directory.GetFiles(shipped, "*.json"))
        {
            var target = Path.Combine(ProfilesDirectory, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveFlume", "settings.json");

    public string ProfilesDirectory { get; }
    public string CalibrationPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "calibration.json");

    public AppSettings Load()
    {
        if (File.Exists(path))
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), FlumeConfig.JsonOptions) ?? new AppSettings();
            return s.Flume.Validate().Count == 0 ? s : s with { Flume = LoadShippedFlume() };
        }
        // First run: start from the shipped limits, then apply what was chosen in the installer.
        var fresh = new AppSettings { Flume = LoadShippedFlume() };
        return InstallDefaults.Load(shippedConfigDirectory)?.Apply(fresh) ?? fresh;
    }

    public void Save(AppSettings settings)
    {
        var errors = settings.Flume.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, FlumeConfig.JsonOptions));
        File.Move(tmp, path, true);
    }

    public TagMap? LoadProfile(string file)
    {
        var p = Path.Combine(ProfilesDirectory, file);
        return File.Exists(p) ? TagMap.Load(p) : null;
    }

    public IReadOnlyList<string> ProfileFiles() =>
        Directory.Exists(ProfilesDirectory)
            ? Directory.GetFiles(ProfilesDirectory, "*.json").Select(Path.GetFileName).OfType<string>().Order().ToList()
            : [];

    private FlumeConfig LoadShippedFlume()
    {
        var p = Path.Combine(shippedConfigDirectory, "flume_config.json");
        return File.Exists(p) ? FlumeConfig.Load(p) : new FlumeConfig();
    }
}
