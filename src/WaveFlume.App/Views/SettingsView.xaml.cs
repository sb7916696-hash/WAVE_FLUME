using System.Windows;
using System.Windows.Controls;
using WaveFlume.App.ViewModels;

namespace WaveFlume.App.Views;

/// <summary>PasswordBox values are passed to the view-model directly and never bound.</summary>
public partial class SettingsView : UserControl
{
    public static readonly DependencyProperty CalibrationContextProperty =
        DependencyProperty.Register(nameof(CalibrationContext), typeof(object), typeof(SettingsView));

    /// <summary>The calibration view-model, shown inside this tab.</summary>
    public object? CalibrationContext
    {
        get => GetValue(CalibrationContextProperty);
        set => SetValue(CalibrationContextProperty, value);
    }

    public SettingsView() => InitializeComponent();

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnOpcPasswordChanged(object sender, RoutedEventArgs e) => Vm?.SetOpcUaPassword(((PasswordBox)sender).Password);
}
