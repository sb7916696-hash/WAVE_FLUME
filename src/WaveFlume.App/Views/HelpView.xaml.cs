using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WaveFlume.App.Services;

namespace WaveFlume.App.Views;

public partial class HelpView : UserControl
{
    private readonly string? _manualPdf;
    private readonly string? _onlineLink;

    public HelpView()
    {
        InitializeComponent();
        var v = typeof(HelpView).Assembly.GetName().Version;
        VersionText.Text = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";

        _manualPdf = ManualLocator.FindPdf(AppContext.BaseDirectory);
        _onlineLink = ManualLocator.ReadOnlineLink(Path.Combine(AppContext.BaseDirectory, "config"));
        if (_manualPdf is null)
        {
            OpenManualButton.IsEnabled = false;
            ManualMissingText.Text = $"The manual file ({ManualLocator.PdfName}) was not found in the docs folder next to the program.";
            ManualMissingText.Visibility = Visibility.Visible;
        }
        if (_onlineLink is not null)
        {
            OnlineLink.NavigateUri = new Uri(_onlineLink);
            OnlineLinkText.Visibility = Visibility.Visible;
        }
    }

    private void OnOpenManual(object sender, RoutedEventArgs e) => Open(_manualPdf, "the User Manual");

    private void OnOnlineLink(object sender, RoutedEventArgs e) => Open(_onlineLink, "the online User Manual");

    private static void Open(string? target, string what)
    {
        if (target is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            MessageBox.Show($"Could not open {what}.\n\n{ex.Message}\n\nIf there is no PDF reader installed, open the file from the docs folder next to the program.",
                "Wave Flume", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
