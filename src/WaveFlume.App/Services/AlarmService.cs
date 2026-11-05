using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using WaveFlume.Core;

namespace WaveFlume.App.Services;

public sealed partial class AlarmEntry(Alert alert) : ObservableObject
{
    public Alert Alert { get; } = alert;
    public AlertSeverity Severity => Alert.Severity;
    public string Time => Alert.UtcTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string Priority => Alert.Severity switch
    {
        AlertSeverity.EStop => "1 E-STOP",
        AlertSeverity.Critical => "2 CRITICAL",
        _ => "3 WARNING",
    };
    public string Source => Alert.Source;
    public string Description => Alert.Description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AckText))]
    private bool _acknowledged;

    public string AckText => Acknowledged ? "Acked" : "Unacked";
}

/// <summary>Alarm/event log (ISA-18.2 style): priority, time, acknowledge state. Phase 5 also writes telemetry_alerts.</summary>
public sealed partial class AlarmService(ILogger<AlarmService> log) : ObservableObject
{
    public const int MaxEntries = 2000;

    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public ObservableCollection<AlarmEntry> Log { get; } = [];

    /// <summary>Every alarm, on the raising thread (the DB writer subscribes).</summary>
    public event Action<Alert>? Raised;

    [ObservableProperty]
    private AlarmEntry? _banner;

    [ObservableProperty]
    private int _activeCount;

    /// <summary>Thread-safe: marshals to the UI thread.</summary>
    public void Raise(Alert alert)
    {
        var level = alert.Severity == AlertSeverity.Warning ? LogLevel.Warning : LogLevel.Error;
        log.Log(level, "Alarm {Severity} {Source}: {Description}", alert.Severity, alert.Source, alert.Description);
        Raised?.Invoke(alert);
        if (_ui is null || SynchronizationContext.Current == _ui) Add(alert);
        else _ui.Post(_ => Add(alert), null);
    }

    public void Raise(AlertSeverity severity, string source, string description) =>
        Raise(new Alert(DateTime.UtcNow, severity, source, description));

    public void Acknowledge(AlarmEntry? entry)
    {
        if (entry is null) return;
        entry.Acknowledged = true;
        Update();
    }

    public void AcknowledgeAll()
    {
        foreach (var e in Log) e.Acknowledged = true;
        Update();
    }

    private void Add(Alert alert)
    {
        Log.Insert(0, new AlarmEntry(alert));
        while (Log.Count > MaxEntries) Log.RemoveAt(Log.Count - 1);
        Update();
    }

    private void Update()
    {
        var active = Log.Where(e => !e.Acknowledged).ToList();
        ActiveCount = active.Count;
        // Highest priority first, newest within a priority (Log is newest-first).
        Banner = active.OrderByDescending(e => e.Severity).FirstOrDefault();
    }
}
