using System.Windows;
using Microsoft.Win32;

namespace WaveFlume.App.Services;

/// <summary>Everything the alarm Details dialog shows.</summary>
public sealed record AlarmDetails(
    string Priority, string Time, string Source, string Message, string Problem, string Solution,
    bool IsCritical, bool Acknowledged);

/// <summary>Dialogs behind an interface so view-models stay testable.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message);
    void Error(string title, string message);
    void Info(string title, string message);
    string? OpenFile(string filter, string? initialDirectory = null);
    string? SaveFile(string filter, string fileName);

    /// <summary>Shows an alarm's problem and solution. Returns true when the operator pressed Acknowledge.</summary>
    bool ShowAlarmDetails(AlarmDetails details);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public bool ShowAlarmDetails(AlarmDetails details) =>
        new Views.AlarmDetailsWindow(details) { Owner = Owner }.ShowDialog() == true;

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Error(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void Info(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? OpenFile(string filter, string? initialDirectory = null)
    {
        var d = new OpenFileDialog { Filter = filter, InitialDirectory = initialDirectory ?? "" };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? SaveFile(string filter, string fileName)
    {
        var d = new SaveFileDialog { Filter = filter, FileName = fileName };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }
}
