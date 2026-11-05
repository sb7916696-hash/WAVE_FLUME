using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaveFlume.App.Services;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.App.ViewModels;

/// <summary>Tab 1: define a wave, calculate and safety-validate it, manage recipes (spec §11.2).</summary>
public sealed partial class WaveGenerationViewModel : ObservableObject
{
    private readonly AppState _app;
    private readonly IRecipeRepository _store;
    private readonly IDialogService _dialogs;
    private readonly AlarmService _alarms;

    public WaveGenerationViewModel(AppState app, IRecipeRepository recipes, IDialogService dialogs, AlarmService alarms)
    {
        (_app, _store, _dialogs, _alarms) = (app, recipes, dialogs, alarms);
        Load(new WaveRequest { SamplingHz = app.Settings.Flume.Limits.CommandFsHz });
        _testNumber = FirstTestNumber;
        Initialization = InitializeAsync();
        var flume = app.Settings.Flume;
        app.PropertyChanged += (_, e) =>
        {
            // Only a change to the flume/limits makes a result stale — not a theme or connection change.
            if (e.PropertyName != nameof(AppState.Settings) || app.Settings.Flume == flume) return;
            flume = app.Settings.Flume;
            OnPropertyChanged(nameof(PeriodLabel));
            OnPropertyChanged(nameof(DepthLabel));
            Invalidate();
        };
    }

    /// <summary>Every wave type the engine can generate.</summary>
    public IReadOnlyList<WaveType> WaveTypes { get; } = Enum.GetValues<WaveType>();

    /// <summary>Every random-wave spectrum: Pierson-Moskowitz, Generalised PM, JONSWAP, TMA and Darbyshire.</summary>
    public IReadOnlyList<SpectralShape> SpectralShapes { get; } = Enum.GetValues<SpectralShape>();
    public IReadOnlyList<RandomPath> RandomPaths { get; } = Enum.GetValues<RandomPath>();

    // ---- inputs -----------------------------------------------------------------------------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFirstOrder))] private WaveOrder _order;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBichromatic), nameof(IsRandom), nameof(IsFocusing), nameof(IsSpectral), nameof(IsUserDefined),
        nameof(AngleAvailable), nameof(NumWavesAvailable), nameof(RampAvailable), nameof(PeriodAvailable), nameof(HeightAvailable), nameof(HeightLabel), nameof(PeriodLabel),
        nameof(OrderAvailable))]
    private WaveType _type;

    /// <summary>2nd-order correction exists for regular waves only; any other type is always 1st order.</summary>
    partial void OnTypeChanged(WaveType value)
    {
        if (value != WaveType.Monochromatic && Order == WaveOrder.Second) Order = WaveOrder.First;
    }

    public bool OrderAvailable => Type == WaveType.Monochromatic;
    [ObservableProperty] private double _waterDepthM;
    [ObservableProperty] private double _waveHeightM;
    [ObservableProperty] private double _periodS;
    [ObservableProperty] private int _numWaves;
    [ObservableProperty] private double _samplingHz;
    [ObservableProperty] private double _signalModPct;
    [ObservableProperty] private double _rampS;
    [ObservableProperty] private double _angleDeg;
    [ObservableProperty] private double _height2M;
    [ObservableProperty] private double _period2S;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowGamma), nameof(ShowBeta))] private SpectralShape _spectrum;
    [ObservableProperty] private double _gamma;
    [ObservableProperty] private double _beta;
    [ObservableProperty] private int _randomSeed;
    [ObservableProperty] private bool _scaleToTargetHs;
    [ObservableProperty] private double _focusXM;
    [ObservableProperty] private double _focusTimeS;
    [ObservableProperty] private RandomPath _path;
    [ObservableProperty] private string? _userFilePath;

    public bool IsFirstOrder
    {
        get => Order == WaveOrder.First;
        set => Order = value ? WaveOrder.First : WaveOrder.Second;
    }

    /// <summary>Gamma shapes the JONSWAP and TMA spectra (TMA is the JONSWAP shape for limited depth); Beta shapes the Generalised PM.</summary>
    public bool ShowGamma => Spectrum is SpectralShape.Jonswap or SpectralShape.Tma;
    public bool ShowBeta => Spectrum == SpectralShape.GeneralizedPm;

    public bool IsBichromatic => Type == WaveType.Bichromatic;
    public bool IsRandom => Type == WaveType.Random;
    public bool IsFocusing => Type == WaveType.Focusing;
    public bool IsSpectral => Type is WaveType.Random or WaveType.Focusing;
    public bool IsUserDefined => Type is WaveType.UserDefinedWave or WaveType.UserDefinedStroke;
    public bool AngleAvailable => Type is WaveType.Monochromatic or WaveType.Bichromatic or WaveType.Random;
    public bool NumWavesAvailable => Type is not (WaveType.Solitary or WaveType.UserDefinedWave or WaveType.UserDefinedStroke);
    public bool PeriodAvailable => NumWavesAvailable;
    public bool HeightAvailable => !IsUserDefined;
    public bool RampAvailable => Type is not (WaveType.Solitary or WaveType.UserDefinedStroke);

    public string HeightLabel => Type switch
    {
        WaveType.Random => "Significant Wave Height Hs (m)",
        WaveType.Focusing => "Focused Wave Height H (m), crest H/2",
        WaveType.Solitary => "Solitary Wave Height H (m)",
        _ => "Signal Wave Height (m)",
    };
    public string PeriodLabel => $"{(IsRandom ? "Peak Period Tp" : "Peak Wave Period")} (s), min {_app.Settings.Flume.EffectiveTMinS:0.00}";
    public string DepthLabel => $"Water Depth (m), max {_app.Settings.Flume.EffectiveDepthRange.Max:0.00}";

    private static readonly HashSet<string> Inputs =
    [
        nameof(Order), nameof(Type), nameof(WaterDepthM), nameof(WaveHeightM), nameof(PeriodS), nameof(NumWaves),
        nameof(SamplingHz), nameof(SignalModPct), nameof(RampS), nameof(AngleDeg), nameof(Height2M), nameof(Period2S),
        nameof(Spectrum), nameof(Gamma), nameof(Beta), nameof(RandomSeed), nameof(ScaleToTargetHs), nameof(FocusXM), nameof(FocusTimeS),
        nameof(Path), nameof(UserFilePath),
    ];

    /// <summary>Any input change invalidates the last calculation, so a stale trajectory can never be sent.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && Inputs.Contains(name)) Invalidate();
    }

    // ---- outputs ----------------------------------------------------------------------------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSend), nameof(ForceKn))] private WaveResult? _result;

    public double? ForceKn => Result?.Wavemaker.ForceEstimateN / 1000;
    [ObservableProperty] private IReadOnlyList<LimitPoint> _limitPoints = [];
    [ObservableProperty] private string _safetyText = "Not calculated.";

    /// <summary>"None", "Ok", "Warning" or "Alarm" — the view maps it to colour plus a text cue.</summary>
    [ObservableProperty] private string _safetyLevel = "None";
    [ObservableProperty] private bool _isBusy;
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> Info { get; } = [];

    public bool CanSend => Result?.Safety.Ok == true;

    public WaveRequest Request => new()
    {
        Order = Order, Type = Type, WaterDepthM = WaterDepthM, WaveHeightM = WaveHeightM, PeriodS = PeriodS,
        NumWaves = NumWaves, SamplingHz = SamplingHz, SignalModPct = SignalModPct, RampS = RampS,
        AngleDeg = AngleAvailable ? AngleDeg : 0,
        Height2M = IsBichromatic ? Height2M : null, Period2S = IsBichromatic ? Period2S : null,
        Spectrum = Spectrum, Gamma = Gamma, Beta = Beta, RandomSeed = RandomSeed, ScaleToTargetHs = ScaleToTargetHs,
        FocusXM = FocusXM, FocusTimeS = FocusTimeS, Path = Path, UserFilePath = IsUserDefined ? UserFilePath : null,
    };

    [RelayCommand]
    private void BrowseUserFile()
    {
        var path = _dialogs.OpenFile("CSV time_s,value (*.csv;*.txt)|*.csv;*.txt|All files|*.*");
        if (path is not null) UserFilePath = path;
    }

    [RelayCommand]
    private async Task CalculateAsync()
    {
        if (SignalModPct > 100 && !_dialogs.Confirm("Signal modification",
                $"% Signal Modification is {SignalModPct:0} % (> 100 %). The paddle motion will exceed the computed stroke. Continue?"))
            return;

        var req = Request;
        var flume = _app.Settings.Flume;
        IsBusy = true;
        try
        {
            var (result, curve) = await Task.Run(() =>
                (new WaveMathEngine(flume).Calculate(req), new LimitCurve(flume).Compute(req.WaterDepthM)));
            Result = result;
            LimitPoints = curve;
            ShowSafety(result.Safety);
            Info.Clear();
            foreach (var line in result.Info) Info.Add(line);
        }
        catch (Exception ex) when (ex is NotImplementedException or ArgumentException or InvalidOperationException
                                       or IOException or InvalidDataException)
        {
            Invalidate();
            SafetyLevel = "Alarm";
            SafetyText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowSafety(SafetyReport report)
    {
        Warnings.Clear();
        foreach (var w in report.Warnings) Warnings.Add(w.Message);
        if (!report.Ok)
        {
            SafetyLevel = "Alarm";
            SafetyText = $"BLOCKED: {report.Violations[0].Message}" +
                         (report.Violations.Count > 1 ? $"  (+{report.Violations.Count - 1} more)" : "");
            foreach (var v in report.Violations)
                _alarms.Raise(AlertSeverity.Warning, AlertSources.MathGatekeeper, $"{v.Code}: {v.Message}");
        }
        else
        {
            SafetyLevel = report.Warnings.Count > 0 ? "Warning" : "Ok";
            SafetyText = report.Warnings.Count > 0 ? $"OK with {report.Warnings.Count} warning(s)." : "OK: within all limits.";
        }
    }

    private void Invalidate()
    {
        if (Result is null && SafetyLevel == "None") return;
        Result = null;
        LimitPoints = [];
        Warnings.Clear();
        Info.Clear();
        SafetyLevel = "None";
        SafetyText = "Inputs changed: press Calculate.";
    }

    [RelayCommand]
    private void Reset() => Load(new WaveRequest { SamplingHz = _app.Settings.Flume.Limits.CommandFsHz });

    private void Load(WaveRequest r)
    {
        // A recipe from an older version may hold a type or spectrum that is not offered; fall back to the defaults.
        bool offered = WaveTypes.Contains(r.Type);
        Type = offered ? r.Type : WaveType.Monochromatic;
        Order = offered && Type == WaveType.Monochromatic ? r.Order : WaveOrder.First;
        WaterDepthM = r.WaterDepthM; WaveHeightM = r.WaveHeightM; PeriodS = r.PeriodS;
        NumWaves = r.NumWaves; SamplingHz = r.SamplingHz; SignalModPct = r.SignalModPct; RampS = r.RampS; AngleDeg = r.AngleDeg;
        Height2M = r.Height2M ?? 0.03; Period2S = r.Period2S ?? 1.2;
        Spectrum = SpectralShapes.Contains(r.Spectrum) ? r.Spectrum : SpectralShape.Jonswap; Gamma = r.Gamma; Beta = r.Beta; RandomSeed = r.RandomSeed; ScaleToTargetHs = r.ScaleToTargetHs;
        FocusXM = r.FocusXM; FocusTimeS = r.FocusTimeS; Path = r.Path; UserFilePath = r.UserFilePath;
        Invalidate();
    }

    // ---- recipes ----------------------------------------------------------------------------

    [ObservableProperty] private string _testNumber;
    [ObservableProperty] private IReadOnlyList<WaveRecipe> _recipes = [];
    [ObservableProperty] private WaveRecipe? _selectedRecipe;

    private const string FirstTestNumber = "T-0001";

    /// <summary>Completes once the recipe list (DB or local JSON) has loaded.</summary>
    public Task Initialization { get; }

    private async Task InitializeAsync()
    {
        await RefreshRecipesAsync();
        TestNumber = NextTestNumber();
    }

    private async Task RefreshRecipesAsync() => Recipes = await _store.ListAsync();

    /// <summary>Next free "T-0001" style number among the loaded recipes.</summary>
    private string NextTestNumber()
    {
        var used = Recipes.Select(r => r.TestNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int n = 1; ; n++)
            if (!used.Contains($"T-{n:0000}")) return $"T-{n:0000}";
    }

    private WaveRecipe Snapshot(string testNumber) => new()
    {
        TestNumber = testNumber,
        Request = Request,
        CalcStrokeM = Result?.Wavemaker.StrokeAmplitudeM,
        CalcVelocityMs = Result?.Wavemaker.PeakVelocityMs,
        CalcAccelMs2 = Result?.Wavemaker.PeakAccelMs2,
        CalcForceKn = Result?.Wavemaker.ForceEstimateN / 1000,
        CalcPowerW = Result?.Wavemaker.PowerW,
        CalcWaveLengthM = Result?.Kinematics?.WaveLengthM,
        CalcCelerityMs = Result?.Kinematics?.CelerityMs,
    };

    private async Task Try(string what, Func<Task> action)
    {
        try
        {
            await action();
            await RefreshRecipesAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            _dialogs.Error(what, ex.Message);
        }
    }

    /// <summary>Save: insert under the typed test number (must be new).</summary>
    [RelayCommand]
    private Task SaveAsync() => Try("Save recipe", () =>
        string.IsNullOrWhiteSpace(TestNumber)
            ? throw new InvalidOperationException("Enter a test number.")
            : _store.InsertAsync(Snapshot(TestNumber.Trim())));

    /// <summary>Add: insert under the next free test number.</summary>
    [RelayCommand]
    private Task AddAsync() => Try("Add recipe", async () =>
    {
        await RefreshRecipesAsync();
        TestNumber = NextTestNumber();
        await _store.InsertAsync(Snapshot(TestNumber));
    });

    [RelayCommand]
    private Task UpdateAsync() => Try("Update recipe", () => _store.UpdateAsync(Snapshot(TestNumber.Trim())));

    /// <summary>Load Prev Wavegen: restores the selected recipe's inputs (recalculate before sending).</summary>
    [RelayCommand]
    private void LoadRecipe()
    {
        if (SelectedRecipe is not { } r) return;
        Load(r.Request);
        TestNumber = r.TestNumber;
    }

    [RelayCommand]
    private void New()
    {
        Reset();
        TestNumber = NextTestNumber();
        SelectedRecipe = null;
    }

    [RelayCommand]
    private Task DeleteRecipeAsync()
    {
        if (SelectedRecipe is not { } r || !_dialogs.Confirm("Delete recipe", $"Delete recipe '{r.TestNumber}'?")) return Task.CompletedTask;
        return Try("Delete recipe", () => _store.DeleteAsync(r.TestNumber));
    }
}
