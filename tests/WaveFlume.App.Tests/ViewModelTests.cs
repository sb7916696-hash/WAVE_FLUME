using FluentAssertions;
using WaveFlume.App.Services;
using WaveFlume.App.ViewModels;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.App.Tests;

public class ViewModelTests
{
    private static WaveGenerationViewModel Gen(TestEnv env)
    {
        var vm = new WaveGenerationViewModel(env.App, env.Recipes, env.Dialogs, env.Alarms);
        vm.Initialization.Wait();
        return vm;
    }

    [Fact]
    public async Task Calculate_golden_case_and_invalidate_on_edit()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.Result!.Wavemaker.StrokeAmplitudeM.Should().BeApproximately(0.0136, 0.0001);
        vm.ForceKn.Should().BeApproximately(0.177, 0.002);
        vm.CanSend.Should().BeTrue();
        vm.SafetyLevel.Should().Be("Ok");

        env.App.SavePreference(env.App.Settings with { Theme = ThemeKind.Dark });
        vm.CanSend.Should().BeTrue("a theme change does not affect the calculation");
        env.App.Save(env.App.Settings with { Flume = env.App.Settings.Flume with { Limits = env.App.Settings.Flume.Limits with { MaxStrokeM = 0.30 } } });
        vm.Result.Should().BeNull("new limits make the result stale");
        await vm.CalculateCommand.ExecuteAsync(null);

        vm.WaveHeightM = 0.06;
        vm.Result.Should().BeNull("a stale trajectory must never be sendable");
        vm.CanSend.Should().BeFalse();
        vm.SafetyText.Should().Contain("Calculate");
    }

    [Fact]
    public async Task Missing_acceleration_limit_blocks_send()
    {
        using var env = new TestEnv(accel: null);
        var vm = Gen(env);
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.CanSend.Should().BeFalse();
        vm.SafetyLevel.Should().Be("Alarm");
        vm.SafetyText.Should().Contain("Acceleration limit not configured");
        env.Alarms.Log.Should().Contain(a => a.Source == AlertSources.MathGatekeeper);
    }

    [Fact]
    public async Task Signal_modification_above_100_needs_confirmation()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        vm.SignalModPct = 120;
        env.Dialogs.ConfirmResult = false;
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.Result.Should().BeNull();
        env.Dialogs.Confirms.Should().ContainSingle().Which.Should().Contain("120");
    }

    [Fact]
    public async Task Every_wave_type_calculates_or_explains_what_is_missing()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        vm.Type = WaveType.Cnoidal;
        vm.WaveHeightM = 0.03; vm.PeriodS = 4; vm.RampS = 2;
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.Result.Should().NotBeNull(vm.SafetyText);

        vm.Type = WaveType.Bichromatic;
        vm.WaveHeightM = 0.05; vm.PeriodS = 1.5; vm.RampS = 3;
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.Result.Should().NotBeNull(vm.SafetyText);

        vm.Type = WaveType.Random;
        vm.PeriodS = 1.5;
        vm.NumWaves = 100;
        vm.RampS = 5;
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.CanSend.Should().BeTrue(vm.SafetyText);
        vm.Info.Should().HaveCount(2).And.Contain(i => i.Contains("energy removed"));
        vm.Result!.TargetSpectrum.Should().NotBeNull();

        vm.Type = WaveType.UserDefinedStroke;
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.SafetyText.Should().Contain("CSV");
    }

    [Fact]
    public async Task Recipe_buttons()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        vm.TestNumber.Should().Be("T-0001");
        vm.PeriodS = 1.5;
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);
        env.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("already exists");

        await vm.AddCommand.ExecuteAsync(null);
        vm.TestNumber.Should().Be("T-0002");
        vm.Recipes.Should().HaveCount(2);

        vm.PeriodS = 2.5;
        await vm.UpdateCommand.ExecuteAsync(null);
        vm.NewCommand.Execute(null);
        vm.PeriodS.Should().Be(1.0);
        vm.SelectedRecipe = vm.Recipes.Single(r => r.TestNumber == "T-0002");
        vm.LoadRecipeCommand.Execute(null);
        vm.PeriodS.Should().Be(2.5);
        vm.TestNumber.Should().Be("T-0002");
    }

    [Fact]
    public void Settings_validation_needs_no_login()
    {
        using var env = new TestEnv();
        var vm = new SettingsViewModel(env.App, env.Dialogs, localIps: () => []);
        vm.SaveCommand.CanExecute(null).Should().BeTrue("anyone can change the settings; there is no login");

        vm.MaxStrokeMm = 460;
        vm.Build().Error.Should().Contain("MaxStrokeM");
        vm.MaxStrokeMm = 300;
        vm.MaxAccelMs2 = "abc";
        vm.Build().Error.Should().Contain("acceleration");
        vm.MaxAccelMs2 = "";
        vm.Build().Settings!.Flume.Limits.MaxAccelMs2.Should().BeNull("blank = not configured");

        vm.Protocol = PlcProtocolType.OmronFins;
        vm.Port.Should().Be(9600);
        vm.TimeoutMs.Should().Be(100, "spec §9.3");
        vm.IpAddress = "192.168.250.300";
        vm.Build().Error.Should().Contain("IPv4");
    }

    [Fact]
    public async Task Settings_network_hint_and_test_connection_on_simulator()
    {
        using var env = new TestEnv();
        var vm = new SettingsViewModel(env.App, env.Dialogs, localIps:
            () => [(System.Net.IPAddress.Parse("192.168.1.5"), System.Net.IPAddress.Parse("255.255.255.0"))]);
        vm.NetworkHintText.Should().BeEmpty("simulator");
        vm.Protocol = PlcProtocolType.ModbusTcp;
        vm.IpAddress = "192.168.250.1";
        vm.NetworkHintText.Should().Contain("not on any PC subnet");

        vm.Protocol = PlcProtocolType.Simulator;
        await vm.TestConnectionCommand.ExecuteAsync(null);
        vm.TestResult.Should().StartWith("OK");
    }

    /// <summary>Full operator sequence against the simulator in real time (~7 s).</summary>
    [Fact]
    public async Task Wave_paddle_full_run_on_simulator()
    {
        using var env = new TestEnv();
        var gen = Gen(env);
        gen.RampS = 1;
        await gen.CalculateCommand.ExecuteAsync(null);
        await using var session = new PlcSession(env.Alarms, env.App);
        var pad = new WavePaddleViewModel(env.App, session, gen, env.Alarms, env.Dialogs);

        double maxMm = 0;
        async Task Tick(int times)
        {
            for (int i = 0; i < times; i++)
            {
                await Task.Delay(100);
                await pad.TickAsync();
                maxMm = Math.Max(maxMm, pad.Paddles.Max(p => Math.Abs(p.Mm)));
            }
        }

        pad.StartCommand.CanExecute(null).Should().BeFalse();
        await pad.ConnectCommand.ExecuteAsync(null);
        await Tick(2);
        session.State.Should().Be(RunState.Connected);
        await pad.EnableServosCommand.ExecuteAsync(null);
        await Tick(2);
        await pad.HomeCommand.ExecuteAsync(null);
        await Tick(2);
        session.State.Should().Be(RunState.Homed);
        pad.SendCommand.CanExecute(null).Should().BeTrue();
        await pad.SendCommand.ExecuteAsync(null);
        await Tick(2);
        session.State.Should().Be(RunState.TrajectoryLoaded);
        pad.UploadProgress.Should().Be(100);

        await pad.StartCommand.ExecuteAsync(null);
        await Tick(20);
        session.State.Should().Be(RunState.Running);
        maxMm.Should().BeInRange(12, 14.5, "X_a = 13.6 mm (paddles in phase, so sample the peak over time)");
        pad.DisconnectBlocked().Should().BeTrue();

        await pad.SmoothStopCommand.ExecuteAsync(null);
        await Tick(45); // 2 s ramp-down + 1 s extra record + margin
        pad.LastRunCsv.Should().NotBeNull();
        var lines = File.ReadAllLines(pad.LastRunCsv!);
        lines.Should().Contain(l => l.StartsWith("# test_number: T-0001"));
        lines.Count(l => !l.StartsWith('#')).Should().BeGreaterThan(400);
        pad.Stats.Single(s => s.Name == "Waves").Final.Should().NotBe("--");
        for (int i = 0; i < 100 && pad.LastReport is null; i++) await Tick(1); // PDF is generated after the CSV closes
        pad.LastReport.Should().NotBeNull().And.EndWith(".pdf");
        File.Exists(pad.LastReport).Should().BeTrue();
        lines.Should().Contain(l => l.StartsWith("# probe_calibration: ch1 nominal"));
        pad.StartCommand.CanExecute(null).Should().BeTrue("trajectory stays loaded for a repeat run");
    }
}

internal static class PaddleTestExtensions
{
    public static bool DisconnectBlocked(this WavePaddleViewModel vm) =>
        !RunStateRules.IsAllowed(vm.Session.State, OperatorAction.Disconnect);
}
