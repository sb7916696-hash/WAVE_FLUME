using System.Windows;
using WaveFlume.App.Services;

namespace WaveFlume.App.Views;

/// <summary>Shows what an alarm means and what to do about it; Acknowledge is under the text.</summary>
public partial class AlarmDetailsWindow : Window
{
    public AlarmDetailsWindow(AlarmDetails d)
    {
        InitializeComponent();
        PriorityText.Text = d.Priority;
        WhenText.Text = $"{d.Time}   {d.Source}";
        Header.Background = (System.Windows.Media.Brush)FindResource(d.IsCritical ? "Alarm" : "Warning");
        var headerText = (System.Windows.Media.Brush)FindResource(d.IsCritical ? "AlarmText" : "WarningText");
        PriorityText.Foreground = headerText;
        WhenText.Foreground = headerText;
        ProblemText.Text = d.Problem;
        RawText.Text = d.Message == d.Problem ? "" : $"Message: {d.Message}";
        RawText.Visibility = RawText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SolutionText.Text = d.Solution;
        if (d.Acknowledged)
        {
            AckButton.Content = "Acknowledged";
            AckButton.IsEnabled = false;
        }
    }

    private void OnAcknowledge(object sender, RoutedEventArgs e) => DialogResult = true;
}
