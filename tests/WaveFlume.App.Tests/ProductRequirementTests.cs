using System.Text.RegularExpressions;
using FluentAssertions;
using WaveFlume.App.Services;
using WaveFlume.App.ViewModels;
using WaveFlume.Core;
using WaveFlume.Plc;

namespace WaveFlume.App.Tests;

/// <summary>Client requirements: every wave type and spectrum offered, clear alarms, PDF message.</summary>
public class ProductRequirementTests
{
    private static WaveGenerationViewModel Gen(TestEnv env)
    {
        var vm = new WaveGenerationViewModel(env.App, env.Recipes, env.Dialogs, env.Alarms);
        vm.Initialization.Wait();
        return vm;
    }

    // ---- what the operator can choose ---------------------------------------------------------

    [Fact]
    public void Every_wave_type_can_be_chosen()
    {
        using var env = new TestEnv();
        Gen(env).WaveTypes.Should().BeEquivalentTo(Enum.GetValues<WaveType>()).And.HaveCount(8);
    }

    [Fact]
    public void Drop_downs_show_plain_names_not_code_names()
    {
        EnumLabelConverter.Label(WaveType.Monochromatic).Should().Contain("Monochromatic").And.NotContain("Type");
        EnumLabelConverter.Label(WaveType.Random).Should().Contain("Random");
        EnumLabelConverter.Label(SpectralShape.Jonswap).Should().Be("JONSWAP");
        EnumLabelConverter.Label(SpectralShape.GeneralizedPm).Should().Be("Generalised PM");
        EnumLabelConverter.Label(RandomPath.PaddleSpectrum).Should().NotBe("PaddleSpectrum");
        EnumLabelConverter.Label(null).Should().BeEmpty();
        foreach (var t in Enum.GetValues<WaveType>()) EnumLabelConverter.Label(t).Should().NotBe(t.ToString()).And.NotBeEmpty();
        foreach (var s in Enum.GetValues<SpectralShape>()) EnumLabelConverter.Label(s).Should().NotBeEmpty();
        EnumLabelConverter.Label(SpectralShape.Tma).Should().Contain("TMA");
    }

    [Fact]
    public void All_five_spectra_can_be_chosen()
    {
        using var env = new TestEnv();
        Gen(env).SpectralShapes.Should().BeEquivalentTo(Enum.GetValues<SpectralShape>()).And.HaveCount(5);
    }

    [Fact]
    public void Gamma_is_shown_for_jonswap_and_beta_for_generalised_pm()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        vm.Spectrum.Should().Be(SpectralShape.Jonswap, "the default spectrum");
        (vm.ShowGamma, vm.ShowBeta).Should().Be((true, false));
        vm.Spectrum = SpectralShape.GeneralizedPm;
        (vm.ShowGamma, vm.ShowBeta).Should().Be((false, true));
        vm.Spectrum = SpectralShape.Tma;
        (vm.ShowGamma, vm.ShowBeta).Should().Be((true, false));
        vm.Spectrum = SpectralShape.Darbyshire;
        (vm.ShowGamma, vm.ShowBeta).Should().Be((false, false));
    }

    [Fact]
    public void Second_order_is_for_regular_waves_only_and_random_is_always_first_order()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        vm.OrderAvailable.Should().BeTrue();
        vm.IsFirstOrder = false;
        vm.Order.Should().Be(WaveOrder.Second);

        vm.Type = WaveType.Random;
        vm.OrderAvailable.Should().BeFalse();
        vm.Order.Should().Be(WaveOrder.First, "switching to Random drops back to 1st order");

        vm.Type = WaveType.Monochromatic;
        vm.OrderAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Monochromatic_second_order_calculates_and_can_be_sent()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        vm.IsFirstOrder = false;
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.CanSend.Should().BeTrue(vm.SafetyText);
    }

    [Fact]
    public void A_saved_cnoidal_recipe_with_a_tma_spectrum_loads_as_saved()
    {
        using var env = new TestEnv();
        env.Recipes.Insert(new WaveRecipe { TestNumber = "OLD", Request = new WaveRequest { Type = WaveType.Cnoidal, Spectrum = SpectralShape.Tma, Order = WaveOrder.First } });
        var vm = Gen(env);
        vm.SelectedRecipe = vm.Recipes.Single(r => r.TestNumber == "OLD");
        vm.LoadRecipeCommand.Execute(null);
        vm.Type.Should().Be(WaveType.Cnoidal);
        vm.Spectrum.Should().Be(SpectralShape.Tma);
    }

    [Theory]
    [InlineData(SpectralShape.Jonswap)] [InlineData(SpectralShape.GeneralizedPm)] [InlineData(SpectralShape.PiersonMoskowitz)]
    [InlineData(SpectralShape.Tma)] [InlineData(SpectralShape.Darbyshire)]
    public async Task Random_wave_calculates_for_every_spectrum_and_a_peak_period_outside_the_range_is_blocked_with_a_clear_message(SpectralShape shape)
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        (vm.Type, vm.Spectrum, vm.WaveHeightM, vm.PeriodS, vm.NumWaves, vm.RampS) = (WaveType.Random, shape, 0.04, 1.5, 100, 4);
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.CanSend.Should().BeTrue(vm.SafetyText);
        vm.Result!.TargetSpectrum.Should().NotBeNull();

        vm.PeriodS = 0.45;
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.CanSend.Should().BeFalse();
        vm.SafetyLevel.Should().Be("Alarm");
        vm.SafetyText.Should().Contain("BLOCKED").And.Contain("outside");
    }

    [Fact]
    public async Task Random_wave_that_loses_energy_to_the_band_limits_shows_a_yellow_warning_but_can_still_be_sent()
    {
        using var env = new TestEnv();
        var vm = Gen(env);
        (vm.Type, vm.WaveHeightM, vm.PeriodS, vm.NumWaves, vm.RampS) = (WaveType.Random, 0.03, 0.8, 100, 4);
        await vm.CalculateCommand.ExecuteAsync(null);
        vm.CanSend.Should().BeTrue(vm.SafetyText);
        vm.SafetyLevel.Should().Be("Warning");
        vm.Warnings.Should().ContainSingle(w => w.Contains("left out"));
    }

    // ---- alarm details -------------------------------------------------------------------------

    [Fact]
    public void Every_safety_check_code_has_a_problem_and_a_solution_of_its_own()
    {
        // Read the codes straight from the gatekeeper source so a new check cannot be added without advice.
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "WaveFlume.sln"))) dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("WaveFlume.sln");
        string source = File.ReadAllText(Path.Combine(dir, "src", "WaveFlume.Physics", "SafetyGatekeeper.cs"))
                        + File.ReadAllText(Path.Combine(dir, "src", "WaveFlume.Physics", "IrregularWaves.cs"));
        var codes = Regex.Matches(source, "new\\(?\\s*\"([A-Z][A-Z_]{3,})\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        codes.Should().Contain(["STROKE", "ACCEL_NOT_CONFIGURED", "BAND_LIMITED", "PERIOD_RANGE"]);

        foreach (var code in codes)
        {
            var advice = AlarmAdvice.For(new Alert(DateTime.UtcNow, AlertSeverity.Warning, AlertSources.MathGatekeeper, $"{code}: something"));
            advice.Problem.Should().NotBe($"{code}: something", $"{code} needs its own wording");
            advice.Solution.Should().NotStartWith("Read the message above", $"{code} needs its own solution");
            advice.Solution.Length.Should().BeGreaterThan(20);
        }
    }

    [Theory]
    [InlineData("Connect failed (Timeout): no reply", "connect")]
    [InlineData("PLC heartbeat lost; Smooth Stop commanded.", "controller")]
    [InlineData("Paddle 3 at 340.0 mm > 95 % of clamp; Smooth Stop commanded.", "paddle")]
    [InlineData("Probe 2 loop fault: 3.10 mA outside [3.8, 20.5] mA; Smooth Stop commanded.", "probe")]
    [InlineData("PLC hard fault (DriveFault); E-stop commanded.", "safety circuit")]
    [InlineData("Software E-STOP pressed by operator.", "Reset Fault")]
    [InlineData("Software E-STOP pressed while not connected — use the hardwired E-stop.", "hardwired")]
    [InlineData("Trajectory upload failed: timeout", "Send to Wave Paddle")]
    [InlineData("Home refused: not enabled", "Enable Servos")]
    [InlineData("Telemetry failed: x. Reconnecting.", "Disconnect and Connect")]
    [InlineData("Communication restored.", "No action")]
    [InlineData("PLC fault: OverTemp.", "Reset Fault")]
    public void Controller_and_drive_alarms_explain_the_problem_and_what_to_do(string text, string solutionMentions)
    {
        var advice = AlarmAdvice.For(new Alert(DateTime.UtcNow, AlertSeverity.Critical, AlertSources.PlcStatus, text));
        advice.Problem.Should().NotBeNullOrWhiteSpace().And.NotBe(text);
        advice.Solution.Should().ContainEquivalentOf(solutionMentions);
    }

    [Fact]
    public void An_unknown_alarm_still_gets_a_problem_and_a_safe_generic_solution()
    {
        var advice = AlarmAdvice.For(new Alert(DateTime.UtcNow, AlertSeverity.Warning, "Whatever", "Something odd happened"));
        advice.Problem.Should().Be("Something odd happened");
        advice.Solution.Should().Contain("service engineer");
    }

    [Fact]
    public async Task Details_dialog_shows_problem_and_solution_and_acknowledges_only_when_the_operator_presses_acknowledge()
    {
        using var env = new TestEnv();
        var main = BuildMain(env, out var session);
        env.Alarms.Raise(AlertSeverity.Critical, AlertSources.MathGatekeeper, "STROKE: Paddle 1 position 0.4 m exceeds clamp ±0.35 m.");
        env.Alarms.Banner.Should().NotBeNull();
        env.Alarms.ActiveCount.Should().Be(1);

        env.Dialogs.AcknowledgeInDetails = false; // operator reads it and closes the window
        main.ShowBannerDetailsCommand.Execute(null);
        var d = env.Dialogs.AlarmDialogs.Should().ContainSingle().Subject;
        d.Problem.Should().Contain("travel further");
        d.Solution.Should().Contain("Reduce the wave height");
        d.Message.Should().Contain("STROKE");
        d.IsCritical.Should().BeTrue();
        d.Acknowledged.Should().BeFalse();
        env.Alarms.ActiveCount.Should().Be(1, "closing the dialog does not acknowledge");

        env.Dialogs.AcknowledgeInDetails = true; // operator presses Acknowledge under the text
        main.ShowBannerDetailsCommand.Execute(null);
        env.Alarms.ActiveCount.Should().Be(0);
        env.Alarms.Banner.Should().BeNull();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Details_from_the_log_opens_the_selected_alarm_and_a_warning_is_not_shown_as_critical()
    {
        using var env = new TestEnv();
        var main = BuildMain(env, out var session);
        env.Alarms.Raise(AlertSeverity.Warning, AlertSources.MathGatekeeper, "BREAKING: Wave height 0.3 m exceeds breaking limit.");
        env.Alarms.Raise(AlertSeverity.Critical, AlertSources.Comms, "PLC heartbeat lost; Smooth Stop commanded.");
        var warning = env.Alarms.Log.Single(a => a.Severity == AlertSeverity.Warning);

        main.ShowAlarmDetailsCommand.Execute(warning);
        var d = env.Dialogs.AlarmDialogs.Single();
        d.IsCritical.Should().BeFalse();
        d.Solution.Should().Contain("longer period");

        env.Alarms.Banner!.Severity.Should().Be(AlertSeverity.Critical, "the banner shows the most serious alarm first");
        await session.DisposeAsync();
    }

    private static MainViewModel BuildMain(TestEnv env, out PlcSession session)
    {
        var gen = Gen(env);
        session = new PlcSession(env.Alarms, env.App);
        var pad = new WavePaddleViewModel(env.App, session, gen, env.Alarms, env.Dialogs);
        return new MainViewModel(env.App, env.Alarms, session, env.Dialogs, gen, pad,
            new SettingsViewModel(env.App, env.Dialogs, localIps: () => []), new CalibrationViewModel(env.App, session, env.Dialogs));
    }

    // ---- which buttons are allowed in which state ---------------------------------------------

    public static IEnumerable<object[]> States() => Enum.GetValues<RunState>().Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(States))]
    public void Button_rules_match_the_documented_table(RunState state)
    {
        // Written out from the user manual, independently of RunStateRules.
        var allowed = new Dictionary<OperatorAction, RunState[]>
        {
            [OperatorAction.Connect] = [RunState.Disconnected],
            [OperatorAction.Disconnect] = [RunState.Connected, RunState.ServosEnabled, RunState.Homed, RunState.TrajectoryLoaded, RunState.Faulted],
            [OperatorAction.EnableServos] = [RunState.Connected],
            [OperatorAction.Home] = [RunState.ServosEnabled, RunState.Homed, RunState.TrajectoryLoaded],
            [OperatorAction.Send] = [RunState.Homed, RunState.TrajectoryLoaded],
            [OperatorAction.Start] = [RunState.TrajectoryLoaded],
            [OperatorAction.SmoothStop] = [RunState.Running],
            [OperatorAction.EStop] = [RunState.Connected, RunState.ServosEnabled, RunState.Homed, RunState.TrajectoryLoaded, RunState.Running, RunState.Stopping, RunState.Faulted],
            [OperatorAction.Reset] = [RunState.Faulted],
        };
        foreach (var action in Enum.GetValues<OperatorAction>())
            RunStateRules.IsAllowed(state, action).Should().Be(allowed[action].Contains(state), $"{action} while {state}");
    }

    // ---- full runs through the real screens on the simulator ----------------------------------

    [Theory]
    [InlineData(SpectralShape.Jonswap)]
    [InlineData(SpectralShape.GeneralizedPm)]
    public async Task Random_run_from_calculate_to_pdf_report_message(SpectralShape shape)
    {
        using var env = new TestEnv();
        var gen = Gen(env);
        (gen.Type, gen.Spectrum, gen.WaveHeightM, gen.PeriodS, gen.NumWaves, gen.RampS, gen.ScaleToTargetHs) =
            (WaveType.Random, shape, 0.03, 1.5, 20, 2, true);
        await gen.CalculateCommand.ExecuteAsync(null);
        gen.CanSend.Should().BeTrue(gen.SafetyText);

        await using var session = new PlcSession(env.Alarms, env.App);
        var pad = new WavePaddleViewModel(env.App, session, gen, env.Alarms, env.Dialogs);
        async Task Tick(int n) { for (int i = 0; i < n; i++) { await Task.Delay(100); await pad.TickAsync(); } }

        pad.SendCommand.CanExecute(null).Should().BeFalse("not connected yet");
        await pad.ConnectCommand.ExecuteAsync(null);
        await Tick(2);
        await pad.EnableServosCommand.ExecuteAsync(null);
        await Tick(2);
        pad.SendCommand.CanExecute(null).Should().BeFalse("not homed yet");
        await pad.HomeCommand.ExecuteAsync(null);
        await Tick(2);
        await pad.SendCommand.ExecuteAsync(null);
        await Tick(2);
        session.State.Should().Be(RunState.TrajectoryLoaded);

        await pad.StartCommand.ExecuteAsync(null);
        await Tick(25);
        session.State.Should().Be(RunState.Running);
        pad.Paddles.Max(p => Math.Abs(p.Mm)).Should().BeGreaterThan(0.1, "the paddles are moving");
        await pad.SmoothStopCommand.ExecuteAsync(null);
        await Tick(60);

        pad.LastRunCsv.Should().NotBeNull();
        File.ReadAllLines(pad.LastRunCsv!).Count(l => !l.StartsWith('#')).Should().BeGreaterThan(200);
        for (int i = 0; i < 150 && pad.LastReport is null; i++) await Tick(1);
        pad.LastReport.Should().NotBeNull();
        env.Dialogs.Infos.Should().BeEmpty("the automatic report at the end of a run does not pop up a message");

        // The PDF Report button tells the operator where the report is and what it is called.
        await pad.ReportCommand.ExecuteAsync(null);
        env.Dialogs.Errors.Should().BeEmpty();
        var message = env.Dialogs.Infos.Should().ContainSingle().Subject;
        message.Should().Contain("saved as a PDF report");
        message.Should().Contain(Path.GetDirectoryName(pad.LastReport)!);
        message.Should().Contain(Path.GetFileName(pad.LastReport)!);
        message.Should().Contain("T-0001").And.EndWith(".pdf");
        File.Exists(pad.LastReport).Should().BeTrue();
    }

    [Fact]
    public async Task Pdf_report_button_with_nothing_to_report_says_so()
    {
        using var env = new TestEnv();
        var gen = Gen(env);
        await using var session = new PlcSession(env.Alarms, env.App);
        var pad = new WavePaddleViewModel(env.App, session, gen, env.Alarms, env.Dialogs);
        await pad.ReportCommand.ExecuteAsync(null);
        env.Dialogs.Infos.Should().BeEmpty();
        env.Dialogs.Errors.Should().ContainSingle().Which.Should().Contain("No run");
    }

    // ---- settings ---------------------------------------------------------------------------

    [Theory]
    [InlineData("0", "positive")]
    [InlineData("-3", "positive")]
    [InlineData("abc", "number")]
    public void Settings_reject_an_unusable_acceleration_limit(string text, string errorContains)
    {
        using var env = new TestEnv();
        var vm = new SettingsViewModel(env.App, env.Dialogs, localIps: () => []);
        vm.MaxAccelMs2 = text;
        vm.Build().Error.Should().Contain(errorContains);
    }

    [Fact]
    public void Settings_accept_a_valid_acceleration_limit_and_save_it_for_the_next_calculation()
    {
        using var env = new TestEnv(accel: null);
        var vm = new SettingsViewModel(env.App, env.Dialogs, localIps: () => []);
        vm.MaxAccelMs2 = "5";
        vm.SaveCommand.Execute(null);
        env.App.Settings.Flume.Limits.MaxAccelMs2.Should().Be(5);
        env.Dialogs.Infos.Should().ContainSingle().Which.Should().Contain("saved");
    }
}
