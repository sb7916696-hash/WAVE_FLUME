using Microsoft.Extensions.Logging.Abstractions;
using WaveFlume.App.Services;
using WaveFlume.Core;

namespace WaveFlume.App.Tests;

public sealed class FakeDialogs : IDialogService
{
    public bool ConfirmResult { get; set; } = true;
    public List<string> Errors { get; } = [];
    public List<string> Confirms { get; } = [];
    public List<string> Infos { get; } = [];
    public string? NextSavePath { get; set; }
    public string? NextOpenPath { get; set; }

    /// <summary>What the operator does in the alarm Details dialog: true = presses Acknowledge, false = closes it.</summary>
    public bool AcknowledgeInDetails { get; set; }
    public List<AlarmDetails> AlarmDialogs { get; } = [];

    public bool Confirm(string title, string message) { Confirms.Add(message); return ConfirmResult; }
    public void Error(string title, string message) => Errors.Add(message);
    public void Info(string title, string message) => Infos.Add(message);
    public string? OpenFile(string filter, string? initialDirectory = null) => NextOpenPath;
    public string? SaveFile(string filter, string fileName) => NextSavePath;
    public bool ShowAlarmDetails(AlarmDetails details) { AlarmDialogs.Add(details); return AcknowledgeInDetails; }
}

/// <summary>Isolated settings/data folder per test.</summary>
public sealed class TestEnv : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"wf_app_{Guid.NewGuid():N}");
    public FakeDialogs Dialogs { get; } = new();
    public AlarmService Alarms { get; } = new(NullLogger<AlarmService>.Instance);
    public AppState App { get; }
    public RecipeStore Recipes { get; }

    public TestEnv(double? accel = 5.0)
    {
        Directory.CreateDirectory(Dir);
        var shipped = Path.Combine(AppContext.BaseDirectory, "config");
        var store = new SettingsStore(Path.Combine(Dir, "settings.json"), shipped);
        App = new AppState(store);
        var flume = App.Settings.Flume;
        App.Save(App.Settings with
        {
            Flume = flume with { Limits = flume.Limits with { MaxAccelMs2 = accel } },
            DataDirectory = Path.Combine(Dir, "data"),
        });
        Recipes = new RecipeStore(Path.Combine(Dir, "data", "recipes.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); }
        catch (IOException) { /* file still held by a logger; temp dir */ }
    }
}
