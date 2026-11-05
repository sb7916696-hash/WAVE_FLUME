using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FluentAssertions;
using WaveFlume.App.Services;
using WaveFlume.App.ViewModels;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.App.Tests;

/// <summary>
/// Stands in for the real <c>App</c> class in tests. The real class runs the program's whole start-up (user settings,
/// log file, main window) as soon as a dispatcher runs, which must never happen inside a test process. This one only
/// carries the same colours, control styles and converters that App.xaml provides.
/// </summary>
internal sealed class TestApplication : Application
{
    public TestApplication()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        foreach (var name in new[] { "Themes/Light.xaml", "Themes/Controls.xaml" })
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WaveFlume;component/{name}", UriKind.Absolute),
            });
        Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        Resources["Not"] = new WaveFlume.App.InverseBoolConverter();
        Resources["NullToCollapsed"] = new WaveFlume.App.NullToCollapsedConverter();
        Resources["EnumIs"] = new WaveFlume.App.EnumIsConverter();
        Resources["EnumLabel"] = new WaveFlume.App.EnumLabelConverter();
    }
}

/// <summary>One long-lived WPF thread with the program's resources, shared by the UI tests.</summary>
internal static class UiHost
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _ = new TestApplication();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }

    public static void Run(Action action)
    {
        Exception? failure = null;
        Ui.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    public static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in All<T>(child)) yield return nested;
        }
    }

    public static Button Button(DependencyObject root, string content) =>
        All<Button>(root).FirstOrDefault(b => b.Content as string == content)
        ?? throw new InvalidOperationException($"No button '{content}' in the window.");

    /// <summary>Presses a button the way a user (or UI Automation) does. Throws when the button is disabled.</summary>
    public static void Press(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        // Invoke only queues the click; let the window process it, as it would between two user actions.
        button.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
    }
}

/// <summary>The real main window with real bindings, driven through its buttons.</summary>
public class UiWiringTests
{
    private sealed class Rig : IDisposable
    {
        public TestEnv Env { get; } = new();
        public PlcSession Session { get; }
        public MainWindow Window { get; }
        public MainViewModel Main { get; }

        public Rig()
        {
            var gen = new WaveGenerationViewModel(Env.App, Env.Recipes, Env.Dialogs, Env.Alarms);
            gen.Initialization.Wait();
            Session = new PlcSession(Env.Alarms, Env.App);
            var pad = new WavePaddleViewModel(Env.App, Session, gen, Env.Alarms, Env.Dialogs);
            Main = new MainViewModel(Env.App, Env.Alarms, Session, Env.Dialogs, gen, pad,
                new SettingsViewModel(Env.App, Env.Dialogs, localIps: () => []), new CalibrationViewModel(Env.App, Session, Env.Dialogs));
            Window = new MainWindow(Main, Env.Dialogs)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, ShowInTaskbar = false,
            };
            Window.WindowState = WindowState.Normal;
            Window.Show();
            Window.UpdateLayout();
        }

        public void Settle() => Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

        public Expander Log => UiHost.All<Expander>(Window).First();
        public DataGrid Grid => (DataGrid)Window.FindName("AlarmGrid");

        public void Dispose()
        {
            Window.Close();
            Task.Run(() => Session.DisposeAsync().AsTask()).Wait();
            Env.Dispose();
        }
    }

    [Fact]
    public void The_ui_tests_never_start_the_real_application_or_touch_the_real_settings()
    {
        // Regression: the tests used to run the real App start-up. With the Dark theme saved in the user's own settings
        // file that raised a "WaveFlume error" dialog on the user's screen and wrote entries to their real log.
        UiHost.Run(() =>
        {
            Application.Current.Should().BeOfType<TestApplication>();
            Application.Current.Should().NotBeOfType<WaveFlume.App.App>();
        });
    }

    [Fact]
    public void The_dark_theme_loads_in_any_host_and_the_light_theme_comes_back()
    {
        UiHost.Run(() =>
        {
            Color Background() => ((SolidColorBrush)Application.Current.Resources["WindowBg"]).Color;
            var light = Background();

            WaveFlume.App.App.ApplyTheme(new AppSettings { Theme = ThemeKind.Dark });
            Background().Should().NotBe(light, "the Dark palette has its own background colour");

            WaveFlume.App.App.ApplyTheme(new AppSettings { Theme = ThemeKind.Light });
            Background().Should().Be(light);
        });
    }

    [Fact]
    public void A_theme_that_cannot_be_found_keeps_the_current_colours_instead_of_crashing()
    {
        UiHost.Run(() =>
        {
            Color Background() => ((SolidColorBrush)Application.Current.Resources["WindowBg"]).Color;
            var before = Background();
            var act = () => WaveFlume.App.App.ApplyTheme(new AppSettings { Theme = (ThemeKind)99 });
            act.Should().NotThrow();
            Background().Should().Be(before);
        });
    }

    [Fact]
    public void Alarm_bar_has_a_details_button_and_no_acknowledge_button()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Env.Alarms.Raise(AlertSeverity.Critical, AlertSources.MathGatekeeper, "STROKE: Paddle 1 position 0.4 m exceeds clamp ±0.35 m.");
            rig.Settle();

            UiHost.All<Button>(rig.Window).Select(b => b.Content as string)
                .Should().NotContain(c => c != null && c.Contains("Acknowledge"), "acknowledging is done inside the Details window");
            var details = UiHost.Button(rig.Window, "_Details");
            details.IsEnabled.Should().BeTrue();
            details.IsVisible.Should().BeTrue();

            UiHost.Press(details);
            var shown = rig.Env.Dialogs.AlarmDialogs.Should().ContainSingle().Subject;
            shown.Problem.Should().Contain("travel further");
            shown.Solution.Should().NotBeNullOrWhiteSpace();
        });
    }

    [Fact]
    public void Details_in_the_alarm_log_opens_the_selected_alarm_even_when_it_is_already_acknowledged()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Env.Alarms.Raise(AlertSeverity.Warning, AlertSources.MathGatekeeper, "BREAKING: Wave height 0.3 m exceeds breaking limit.");
            rig.Env.Alarms.AcknowledgeAll(); // the alarm bar is now empty, so only the log can show it
            rig.Log.IsExpanded = true;
            rig.Window.UpdateLayout();
            rig.Settle();
            rig.Window.FindName("AlarmGrid").Should().NotBeNull();

            var details = UiHost.Button(rig.Window, "De_tails");
            rig.Grid.SelectedItem = rig.Grid.Items[0];
            rig.Settle();
            details.IsEnabled.Should().BeTrue("a row is selected");

            UiHost.Press(details);
            var shown = rig.Env.Dialogs.AlarmDialogs.Should().ContainSingle().Subject;
            shown.Message.Should().Contain("BREAKING");
            shown.Acknowledged.Should().BeTrue();
        });
    }

    [Fact]
    public void Details_in_the_alarm_log_is_greyed_until_a_row_is_selected()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Env.Alarms.Raise(AlertSeverity.Warning, AlertSources.Comms, "Communication restored.");
            rig.Env.Alarms.AcknowledgeAll();
            rig.Log.IsExpanded = true;
            rig.Window.UpdateLayout();
            rig.Settle();

            var details = UiHost.Button(rig.Window, "De_tails");
            rig.Grid.SelectedItem = null;
            rig.Settle();
            details.IsEnabled.Should().BeFalse("nothing is selected, so there is nothing to show");

            rig.Grid.SelectedItem = rig.Grid.Items[0];
            rig.Settle();
            details.IsEnabled.Should().BeTrue();
        });
    }

    [Fact]
    public void Double_clicking_a_row_in_the_alarm_log_opens_its_details()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Env.Alarms.Raise(AlertSeverity.Critical, AlertSources.Comms, "PLC heartbeat lost; Smooth Stop commanded.");
            rig.Env.Alarms.AcknowledgeAll();
            rig.Log.IsExpanded = true;
            rig.Window.UpdateLayout();
            rig.Settle();

            var row = UiHost.All<DataGridRow>(rig.Grid).First();
            row.IsSelected = true;
            row.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent, Source = row,
            });
            rig.Env.Dialogs.AlarmDialogs.Should().ContainSingle().Which.Problem.Should().Contain("controller");
        });
    }

    [Fact]
    public void Starting_position_button_replaces_home_and_the_old_wording_is_gone_from_the_screen()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Window.Tabs.SelectedIndex = 1;
            rig.Window.UpdateLayout();
            var contents = UiHost.All<Button>(rig.Window).Select(b => b.Content as string).Where(c => c != null).ToList();
            contents.Should().Contain("Starting Positi_on");
            contents.Should().NotContain(c => c!.Replace("_", "").Equals("Home"));
            rig.Window.Title.Should().Be("Wave Flume");
        });
    }

    [Fact]
    public void There_is_no_admin_login_no_mode_label_and_every_setting_can_be_edited()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Window.Tabs.SelectedIndex = 2;
            rig.Window.UpdateLayout();

            var texts = UiHost.All<TextBlock>(rig.Window).Select(t => t.Text).ToList();
            texts.Should().NotContain(t => t.Contains("Mode:") || t.Contains("Admin") || t.Contains("Operator"));
            UiHost.All<GroupBox>(rig.Window).Select(g => g.Header as string).Should().NotContain("Admin Access");
            UiHost.All<Button>(rig.Window).Select(b => b.Content as string).Should().NotContain(c => c != null && (c.Contains("Login") || c.Contains("Logout")));
            UiHost.All<PasswordBox>(rig.Window).Should().HaveCount(1, "only the OPC UA connection password remains");

            // The groups that used to be locked until login are all editable, and so is Save Settings.
            foreach (var header in new[] { "Wave Paddle and Limits", "Controller Connection", "Range Bounds (min - max)", "TagMap (logical signal to PLC address)" })
                UiHost.All<GroupBox>(rig.Window).Single(g => g.Header as string == header).IsEnabled.Should().BeTrue(header);
            UiHost.Button(rig.Window, "Sa_ve Settings").IsEnabled.Should().BeTrue();
        });
    }

    [Fact]
    public void Max_acceleration_can_be_typed_and_saved_straight_away()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Env.App.Save(rig.Env.App.Settings with
            {
                Flume = rig.Env.App.Settings.Flume with { Limits = rig.Env.App.Settings.Flume.Limits with { MaxAccelMs2 = null } },
            });
            rig.Window.Tabs.SelectedIndex = 2;
            rig.Window.UpdateLayout();
            rig.Main.Settings.MaxAccelMs2 = "3.5";
            UiHost.Press(UiHost.Button(rig.Window, "Sa_ve Settings"));
            rig.Env.App.Settings.Flume.Limits.MaxAccelMs2.Should().Be(3.5);
            rig.Env.Dialogs.Infos.Should().ContainSingle().Which.Should().Contain("saved");
        });
    }

    [Fact]
    public void Calculate_and_reset_are_visible_and_no_step_label_is_shown()
    {
        UiHost.Run(() =>
        {
            using var rig = new Rig();
            rig.Window.Tabs.SelectedIndex = 0;
            rig.Window.UpdateLayout();
            UiHost.Button(rig.Window, "_Calculate").IsVisible.Should().BeTrue();
            UiHost.All<TextBlock>(rig.Window).Select(t => t.Text).Should().NotContain(t => t.StartsWith("Step "));
        });
    }
}
