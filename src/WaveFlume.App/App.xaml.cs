using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WaveFlume.App.Services;
using WaveFlume.App.ViewModels;

namespace WaveFlume.App;

public partial class App : Application
{
    private IHost? _host;

    /// <summary>Raised after the palette or plot style changes, so plots can restyle.</summary>
    public static event Action? ThemeChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--extract-native"))
        {
            // Release script: writes plctag.dll next to the exe so the installer can ship it.
            WaveFlume.Plc.EtherNetIpDriver.EnsureNativeLibrary();
            Shutdown();
            return;
        }
        string? shotDir = Arg(e.Args, "--screenshots");
        string settingsPath = Arg(e.Args, "--settings")
                              ?? (shotDir is null ? SettingsStore.DefaultPath : Path.Combine(Path.GetFullPath(shotDir), "settings.json"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(Path.GetDirectoryName(settingsPath)!, "logs", "waveflume-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
            .CreateLogger();
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error(ex.Exception, "Unhandled UI exception");
            MessageBox.Show(ex.Exception.Message, "WaveFlume error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(s =>
            {
                s.AddSingleton(new SettingsStore(settingsPath, Path.Combine(AppContext.BaseDirectory, "config")));
                s.AddSingleton<AppState>();
                s.AddSingleton<AlarmService>();
                s.AddSingleton<PlcSession>();
                s.AddSingleton<IDialogService, DialogService>();
                s.AddSingleton(sp => new RecipeStore(Path.Combine(sp.GetRequiredService<AppState>().Settings.DataDirectory, "recipes.json")));
                s.AddSingleton<IRecipeRepository>(sp => sp.GetRequiredService<RecipeStore>());
                s.AddSingleton<CalibrationViewModel>();
                s.AddSingleton<WaveGenerationViewModel>();
                s.AddSingleton<WavePaddleViewModel>();
                s.AddSingleton<SettingsViewModel>();
                s.AddSingleton<MainViewModel>();
                s.AddSingleton<MainWindow>();
            })
            .Build();

        var state = _host.Services.GetRequiredService<AppState>();
        ApplyTheme(state.Settings);
        state.PropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(AppState.Settings)) ApplyTheme(state.Settings);
        };

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        Log.Information("WaveFlume started; settings {Path}", settingsPath);
        Log.Information("Controller {Protocol} {Ip}; TagMap {Profile}; data folder {Data}",
            state.Settings.Connection.Protocol, state.Settings.Connection.IpAddress, state.Settings.Connection.TagMapProfile, state.Settings.DataDirectory);

        if (shotDir is not null)
        {
            await ScreenshotRunner.RunAsync(window, _host.Services, shotDir);
            Shutdown();
        }
    }

    public static void ApplyTheme(AppSettings settings)
    {
        var dicts = Current.Resources.MergedDictionaries;
        string name = $"Themes/{settings.Theme}.xaml";
        if (dicts[0].Source?.OriginalString.EndsWith(name, StringComparison.OrdinalIgnoreCase) != true)
        {
            // A full pack address names the program's own resources, so it resolves whatever process hosts the window.
            var uri = new Uri($"pack://application:,,,/WaveFlume;component/{name}", UriKind.Absolute);
            try
            {
                dicts[0] = new ResourceDictionary { Source = uri };
            }
            catch (IOException ex)
            {
                // A missing theme must never stop the program: keep the colours that are already showing.
                Log.Warning(ex, "Could not load the {Theme} theme; keeping the current one", settings.Theme);
            }
        }
        PlotStyle.Oscilloscope = settings.OscilloscopePlots;
        ThemeChanged?.Invoke();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.Services.GetRequiredService<PlcSession>().DisposeAsync();
            _host.Dispose();
        }
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
