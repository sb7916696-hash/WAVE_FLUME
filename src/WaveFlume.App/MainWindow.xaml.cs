using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WaveFlume.App.Services;
using WaveFlume.App.ViewModels;

namespace WaveFlume.App;

public partial class MainWindow : Window
{
    /// <summary>Status/state refresh. Plots have their own 16 ms timers.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly MainViewModel _vm;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _timer;
    private bool _ticking;

    public MainWindow(MainViewModel vm, IDialogService dialogs)
    {
        InitializeComponent();
        (_vm, _dialogs) = (vm, dialogs);
        DataContext = vm;
        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick, Dispatcher);
        _timer.Start();
    }

    public System.Windows.Controls.TabControl Tabs => MainTabs;

    /// <summary>Double-clicking a row of the alarm / event log opens that alarm's details.</summary>
    private void OnAlarmRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGridRow { Item: AlarmEntry entry } && _vm.ShowAlarmDetailsCommand.CanExecute(entry))
        {
            _vm.ShowAlarmDetailsCommand.Execute(entry);
            e.Handled = true;
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_ticking) return; // never overlap ticks
        _ticking = true;
        try { await _vm.TickAsync(); }
        finally { _ticking = false; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsRunning && !_dialogs.Confirm("Exit", "A wave run is in progress. Exiting stops telemetry and logging. Exit anyway?"))
            e.Cancel = true;
        base.OnClosing(e);
    }
}
