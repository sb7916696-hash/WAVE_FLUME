using System.Numerics;
using WaveFlume.Core;

namespace WaveFlume.Physics;

public sealed record SpectrumCurve(double[] FrequencyHz, double[] DensityM2PerHz);

public sealed record RandomWaveInfo(
    int Seed, int Components, double DeltaFHz, double RepeatPeriodS, double RecordS,
    double FLoHz, double FHiHz, double EnergyRemovedPct,
    double TargetHsM, double RealisedH13M, double RealisedHm0M, double ScaleFactor);

/// <summary>Output of a generator, before the gatekeeper.</summary>
public sealed record GeneratedWave(
    Trajectory Trajectory,
    double[] PredictedEtaM,
    IReadOnlyList<WaveComponent> Design,
    IReadOnlyList<WaveComponent> Spectrum)
{
    /// <summary>Heights checked against the Settings range (design heights unless overridden).</summary>
    public IReadOnlyList<double>? Heights { get; init; }
    public bool UsesNumWaves { get; init; } = true;
    public SpectrumCurve? Target { get; init; }
    public RandomWaveInfo? Random { get; init; }
    public IReadOnlyList<string> Info { get; init; } = [];
    public IReadOnlyList<SafetyIssue> Warnings { get; init; } = [];
    public double SecondOrderAmplitudeM { get; init; }
}

/// <summary>Random (§7.6), focusing and user-defined waves/stroke (§7.5).</summary>
public static class IrregularWaves
{
    public const int TargetCurvePoints = 400;

    /// <summary>A random sea that loses more than this share of its energy to the band limits gets a warning.</summary>
    public const double BandLimitWarnPct = 5;

    private static SafetyIssue BandLimitedWarning(double removedPct, double fLoHz, double fHiHz, bool scaled) => new(
        "BAND_LIMITED",
        $"{removedPct:0} % of the requested sea-state energy lies outside {fLoHz:0.000}-{fHiHz:0.000} Hz, the frequencies this wavemaker can make, and is left out. " +
        (scaled
            ? "The remaining waves are scaled up to reach the requested Hs, so the sea differs from the chosen spectrum."
            : $"The sea made will have an Hs about {100 * (1 - Math.Sqrt(1 - removedPct / 100)):0} % lower than requested. Use a peak period nearer the middle of the range."));

    /// <summary>f_lo = max(1/T_max, low·f_p), f_hi = min(1/T_min_eff, high·f_p).</summary>
    public static (double Lo, double Hi) Band(FlumeConfig cfg, double fp) =>
        (Math.Max(1 / cfg.EffectiveTMaxS, cfg.Irregular.BandLowFactor * fp),
         Math.Min(1 / cfg.EffectiveTMinS, cfg.Irregular.BandHighFactor * fp));

    public static GeneratedWave Random(WaveRequest req, FlumeConfig cfg, double angleRad, double gain)
    {
        var spec = Spec(req, cfg);
        var s = Spectra.Normalised(spec);
        var (n, length, df, envelope) = Grid(req, req.NumWaves * req.PeriodS, gain);
        var (lo, hi) = Band(cfg, spec.Fp);
        double m0 = req.WaveHeightM * req.WaveHeightM / 16;
        double removedPct = 100 * (1 - Spectra.Integrate(s, lo, hi, 20_000) / m0);

        // A_j = √(2 S(f_j) Δf), phases ε_j ~ U(0, 2π) from System.Random(seed), ascending frequency.
        var rng = new Random(req.RandomSeed);
        var z = new Complex[length / 2 + 1];
        for (int j = 1; j < length / 2; j++)
        {
            double f = j * df;
            if (f < lo || f > hi) continue;
            z[j] = Complex.FromPolarCoordinates(Math.Sqrt(2 * s(f) * df), -rng.NextDouble() * 2 * Math.PI);
        }

        if (req.Path == RandomPath.SurfaceThenTransfer)
        {
            // Path B: S(f) → η(t) over one full period → FFT → transfer function (below).
            z = SpectralSynthesis.SurfaceBins(SpectralSynthesis.Surface(z, length, length), length);
            SpectralSynthesis.LimitBand(z, length, req.SamplingHz, lo, hi);
        }

        var eta = Multiply(SpectralSynthesis.Surface(z, length, n), envelope);
        var stats = WaveStatistics.ZeroUpcrossing(eta, req.SamplingHz, req.RampS, req.RampS);
        double realised = stats.H13M / gain;
        double scale = req.ScaleToTargetHs && realised > 0 ? req.WaveHeightM / realised : 1;
        if (scale != 1)
        {
            for (int j = 0; j < z.Length; j++) z[j] *= scale;
            for (int i = 0; i < eta.Length; i++) eta[i] *= scale;
        }
        double hm0 = 4 * Std(eta, (int)(req.RampS * req.SamplingHz)) / gain;

        var info = new RandomWaveInfo(req.RandomSeed, z.Count(c => c != Complex.Zero), df, length / req.SamplingHz,
            (n - 1) / req.SamplingHz, lo, hi, removedPct, req.WaveHeightM, realised * scale, hm0, scale);
        var generated = Build(req, cfg, z, length, n, envelope, angleRad, eta) with
        {
            Design = [WaveComponent.Create(req.WaveHeightM, req.PeriodS, req.WaterDepthM, cfg)],
            Target = Curve(s, hi),
            Random = info,
            Warnings = removedPct > BandLimitWarnPct ? [BandLimitedWarning(removedPct, lo, hi, req.ScaleToTargetHs)] : [],
            Info =
            [
                $"Random {spec.Shape}: {info.Components} components, Δf = {df:0.00000} Hz, band {lo:0.000}–{hi:0.000} Hz ({removedPct:0.0} % energy removed), seed {req.RandomSeed}, {req.Path}.",
                $"Repeat period {info.RepeatPeriodS:0.0} s; record {info.RecordS:0.0} s. Realised H1/3 = {info.RealisedH13M:0.0000} m, Hm0 = {hm0:0.0000} m (target Hs {req.WaveHeightM:0.0000} m){(scale != 1 ? $", scaled ×{scale:0.000}" : "")}.",
            ],
        };
        return generated;
    }

    /// <summary>Phase-focused group: φ_i = −k_i x_f + ω_i t_f; Σ a_i = H/2 (linear crest amplitude at the focus).</summary>
    public static GeneratedWave Focusing(WaveRequest req, FlumeConfig cfg, double gain)
    {
        double duration = req.NumWaves * req.PeriodS;
        if (req.FocusTimeS <= req.RampS || req.FocusTimeS >= duration - req.RampS)
            throw new ArgumentOutOfRangeException(nameof(req), $"Focus time must lie between the ramps ({req.RampS} … {duration - req.RampS:0.0} s).");
        var spec = Spec(req, cfg);
        var s = Spectra.Normalised(spec);
        var (n, length, df, envelope) = Grid(req, duration, gain);
        var (lo, hi) = Band(cfg, spec.Fp);

        var amp = new double[length / 2 + 1];
        for (int j = 1; j < length / 2; j++)
        {
            double f = j * df;
            if (f >= lo && f <= hi) amp[j] = s(f) * df;
        }
        double sum = amp.Sum(), crest = req.WaveHeightM / 2;
        var z = new Complex[amp.Length];
        for (int j = 1; j < amp.Length; j++)
        {
            if (amp[j] == 0) continue;
            var w = Dispersion.Solve(1 / (j * df), req.WaterDepthM, cfg.Gravity);
            double phi = -w.K * req.FocusXM + w.Omega * req.FocusTimeS;
            z[j] = Complex.FromPolarCoordinates(crest * amp[j] / sum, -phi);
        }
        var eta = Multiply(SpectralSynthesis.Surface(z, length, n), envelope);
        return Build(req, cfg, z, length, n, envelope, 0, eta) with
        {
            Design = [WaveComponent.Create(req.WaveHeightM, req.PeriodS, req.WaterDepthM, cfg)],
            Target = Curve(s, hi),
            Info =
            [
                $"Focusing ({spec.Shape} amplitudes): {z.Count(c => c != Complex.Zero)} components, band {lo:0.000}–{hi:0.000} Hz; linear crest {crest:0.0000} m at x_f = {req.FocusXM} m, t_f = {req.FocusTimeS} s.",
            ],
        };
    }

    /// <summary>User η(t): FFT → divide by c0(k(f)h) → inverse FFT, band-limited to [1/T_max, 1/T_min_eff].</summary>
    public static GeneratedWave UserWave(WaveRequest req, FlumeConfig cfg, double gain)
    {
        var series = UserSeries.Load(RequirePath(req));
        double fs = req.SamplingHz;
        var raw = series.Resample(0, fs);
        double duration = (raw.Length - 1) / fs;
        CheckRamp(req.RampS, duration);
        var ramped = new double[raw.Length];
        for (int i = 0; i < raw.Length; i++) ramped[i] = raw[i] * Ramp.Envelope(i / fs, duration, req.RampS);

        int length = SpectralSynthesis.FftLength(2 * raw.Length); // zero padding: no wrap-around
        var z = SpectralSynthesis.SurfaceBins(ramped, length);
        double lo = 1 / cfg.EffectiveTMaxS, hi = 1 / cfg.EffectiveTMinS;
        double removed = 100 * SpectralSynthesis.LimitBand(z, length, fs, lo, hi);
        var envelope = Envelope(raw.Length, fs, duration, req.RampS, gain);
        var eta = Multiply(SpectralSynthesis.Surface(z, length, raw.Length), envelope);

        int peak = Enumerable.Range(1, z.Length - 1).MaxBy(j => z[j].Magnitude);
        double hm0 = 4 * Math.Sqrt(z.Sum(c => c.Magnitude * c.Magnitude) / 2);
        var design = hm0 > 0 ? new[] { WaveComponent.Create(hm0, length / (peak * fs), req.WaterDepthM, cfg) } : [];
        return Build(req, cfg, z, length, raw.Length, envelope, 0, eta) with
        {
            Design = design,
            UsesNumWaves = false,
            Info = [$"User wave: {Path.GetFileName(req.UserFilePath)}, {duration:0.0} s, Hm0 {hm0:0.0000} m, Tp {length / (peak * fs):0.000} s; band {lo:0.000}–{hi:0.000} Hz ({removed:0.0} % energy removed)."],
        };
    }

    /// <summary>User X(t) used directly (one column = all paddles, or one per paddle); still safety-checked.</summary>
    public static GeneratedWave UserStroke(WaveRequest req, FlumeConfig cfg, double gain)
    {
        var series = UserSeries.Load(RequirePath(req));
        int paddles = cfg.NumPaddles;
        if (series.Columns.Length != 1 && series.Columns.Length != paddles)
            throw new InvalidDataException($"Stroke file needs 1 or {paddles} value columns, found {series.Columns.Length}.");
        double fs = req.SamplingHz;
        var rows = new float[paddles][];
        var mean = new double[0];
        for (int p = 0; p < paddles; p++)
        {
            var x = series.Resample(series.Columns.Length == 1 ? 0 : p, fs);
            if (mean.Length == 0) mean = new double[x.Length];
            rows[p] = Array.ConvertAll(x, v => (float)(v * gain));
            for (int i = 0; i < x.Length; i++) mean[i] += x[i] * gain / paddles;
        }
        var eta = SpectralSynthesis.SurfaceFromPaddle(mean, fs, req.WaterDepthM, cfg, 1 / cfg.EffectiveTMaxS, 1 / cfg.EffectiveTMinS);
        return new GeneratedWave(new Trajectory(rows, fs), eta, [], [])
        {
            UsesNumWaves = false,
            Info = [$"User stroke: {Path.GetFileName(req.UserFilePath)}, {series.DurationS:0.0} s, {series.Columns.Length} column(s), used directly (no ramp added)."],
        };
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static SpectrumSpec Spec(WaveRequest req, FlumeConfig cfg)
    {
        return new SpectrumSpec(req.Spectrum, req.WaveHeightM, req.PeriodS, req.Gamma, req.Beta, req.WaterDepthM, cfg.Gravity);
    }

    private static (int N, int Length, double Df, double[] Envelope) Grid(WaveRequest req, double duration, double gain)
    {
        CheckRamp(req.RampS, duration);
        double fs = req.SamplingHz;
        int n = (int)Math.Round(duration * fs) + 1;
        int length = SpectralSynthesis.FftLength(n);
        return (n, length, fs / length, Envelope(n, fs, (n - 1) / fs, req.RampS, gain));
    }

    private static double[] Envelope(int n, double fs, double duration, double rampS, double gain)
    {
        var e = new double[n];
        for (int i = 0; i < n; i++) e[i] = gain * Ramp.Envelope(i / fs, duration, rampS);
        return e;
    }

    private static GeneratedWave Build(WaveRequest req, FlumeConfig cfg, Complex[] z, int length, int n,
        double[] envelope, double angleRad, double[] eta)
    {
        double fs = req.SamplingHz;
        var t = SpectralSynthesis.Transfer(z, length, fs, req.WaterDepthM, cfg);
        var paddles = SpectralSynthesis.Paddles(z, t, length, cfg.PaddleCentres(), angleRad, envelope);
        var spectrum = new List<WaveComponent>();
        for (int j = 1; j < z.Length; j++)
        {
            if (z[j] == Complex.Zero) continue;
            var w = new DispersionResult(2 * Math.PI * j * fs / length, t.K[j], req.WaterDepthM);
            spectrum.Add(new WaveComponent(2 * z[j].Magnitude, w, t.C0[j]));
        }
        return new GeneratedWave(new Trajectory(paddles, fs), eta, [], spectrum);
    }

    private static SpectrumCurve Curve(Func<double, double> s, double fHi)
    {
        var f = Enumerable.Range(1, TargetCurvePoints).Select(i => 1.5 * fHi * i / TargetCurvePoints).ToArray();
        return new SpectrumCurve(f, f.Select(s).ToArray());
    }

    private static void CheckRamp(double rampS, double duration)
    {
        if (rampS < 0 || rampS > duration / 2)
            throw new ArgumentOutOfRangeException(nameof(rampS), rampS, $"Ramp duration must be in [0, {duration / 2:0.###}] s (half the signal duration).");
    }

    private static string RequirePath(WaveRequest req) =>
        string.IsNullOrWhiteSpace(req.UserFilePath) ? throw new ArgumentException("Choose a user-defined CSV file.") : req.UserFilePath;

    private static double[] Multiply(double[] x, double[] e)
    {
        for (int i = 0; i < x.Length; i++) x[i] *= e[i];
        return x;
    }

    private static double Std(double[] x, int skip)
    {
        var mid = x.AsSpan(Math.Min(skip, x.Length / 2), Math.Max(1, x.Length - 2 * skip));
        double mean = 0, sumSq = 0;
        foreach (var v in mid) mean += v;
        mean /= mid.Length;
        foreach (var v in mid) sumSq += (v - mean) * (v - mean);
        return Math.Sqrt(sumSq / mid.Length);
    }
}
