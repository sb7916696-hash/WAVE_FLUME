using WaveFlume.Core;

namespace WaveFlume.Physics;

public sealed record WaveKinematics(
    double K, double Kh, double WaveLengthM, double CelerityMs, double GroupVelocityMs,
    DepthRegime Regime, double UMaxMs, double UMaxExactMs);

/// <summary>
/// Wavemaker panel values, including % signal modification. Regular waves: from the formulas (sum of component
/// amplitudes, an upper bound for bichromatic; second-order term included). Other types: stroke, velocity and
/// acceleration are the exact maxima of the generated trajectory. Null = not applicable.
/// </summary>
public sealed record WavemakerOutputs(
    double? C0, double StrokeAmplitudeM, double StrokePeakToPeakM,
    double PeakVelocityMs, double PeakAccelMs2,
    double? ForceEstimateN, double? PowerW, double? PowerPerPaddleW);

public sealed record WaveResult(
    IReadOnlyList<WaveComponent> Components,
    WaveKinematics? Kinematics,
    WavemakerOutputs Wavemaker,
    Trajectory Trajectory,
    SafetyReport Safety,
    double[] PredictedEtaM,
    SpectrumCurve? TargetSpectrum,
    RandomWaveInfo? Random,
    IReadOnlyList<string> Info);

/// <summary>UI-free entry point: WaveRequest → generator → trajectory → safety report (spec §6).</summary>
public sealed class WaveMathEngine(FlumeConfig cfg)
{
    /// <summary>kh below π/10 (h/L &lt; 1/20) is shallow, above π (h/L &gt; 1/2) is deep.</summary>
    public const double ShallowKh = Math.PI / 10;
    public const double DeepKh = Math.PI;

    public WaveResult Calculate(WaveRequest req)
    {
        if (cfg.PaddleType == PaddleType.Flap && cfg.HingeHeightM >= req.WaterDepthM)
            throw new ArgumentOutOfRangeException(nameof(req), "Hinge height must be below the water depth.");
        if (req.SignalModPct <= 0)
            throw new ArgumentOutOfRangeException(nameof(req), "% Signal Modification must be positive.");
        if (req.Order == WaveOrder.Second)
        {
            if (req.Type != WaveType.Monochromatic) throw SecondOrder.SchaefferUnavailable(req.Type);
            if (cfg.PaddleType != PaddleType.Piston)
                throw new NotImplementedException("2nd order uses Madsen (1971), which is for a piston paddle only.");
        }

        double gain = req.SignalModPct / 100;
        bool oblique = req.Type is WaveType.Monochromatic or WaveType.Bichromatic or WaveType.Random;
        double angle = oblique ? Units.DegToRad(req.AngleDeg) : 0;
        var g = req.Type switch
        {
            WaveType.Monochromatic or WaveType.Bichromatic => Regular(req, angle, gain),
            WaveType.Random => IrregularWaves.Random(req, cfg, angle, gain),
            WaveType.Focusing => IrregularWaves.Focusing(req, cfg, gain),
            WaveType.Solitary => SolitaryWave.Generate(req, cfg, gain),
            WaveType.UserDefinedWave => IrregularWaves.UserWave(req, cfg, gain),
            WaveType.UserDefinedStroke => IrregularWaves.UserStroke(req, cfg, gain),
            WaveType.Cnoidal => CnoidalWave.Generate(req, cfg, gain),
            _ => throw new ArgumentOutOfRangeException(nameof(req)),
        };

        var safety = new SafetyGatekeeper(cfg).Validate(req, g, g.Trajectory);
        bool regular = req.Type is WaveType.Monochromatic or WaveType.Bichromatic;
        var wavemaker = regular ? Wavemaker(g.Design, gain, g.SecondOrderAmplitudeM) : FromTrajectory(g, safety.Worst, gain);
        var kinematics = g.Design.Count > 0 ? Kinematics(g.Design[0]) : null;
        return new WaveResult(g.Design, kinematics, wavemaker, g.Trajectory, safety, g.PredictedEtaM, g.Target, g.Random, g.Info);
    }

    private GeneratedWave Regular(WaveRequest req, double angle, double gain)
    {
        var components = req.Type == WaveType.Monochromatic
            ? new[] { WaveComponent.Create(req.WaveHeightM, req.PeriodS, req.WaterDepthM, cfg) }
            : new[]
            {
                WaveComponent.Create(req.WaveHeightM, req.PeriodS, req.WaterDepthM, cfg),
                WaveComponent.Create(
                    req.Height2M ?? throw new ArgumentException("Bichromatic needs H2.", nameof(req)),
                    req.Period2S ?? throw new ArgumentException("Bichromatic needs T2.", nameof(req)),
                    req.WaterDepthM, cfg),
            };

        double x2 = 0;
        var info = new List<string>();
        if (req.Order == WaveOrder.Second)
        {
            var c = components[0];
            x2 = SecondOrder.MadsenPistonAmplitude(c.HeightM, req.WaterDepthM, c.Wave.Kh, c.C0);
            info.Add($"2nd order (Madsen 1971 [VERIFY]): X2 = {Units.MToMm(x2):0.000} mm at 2ω; Stokes bound harmonic " +
                     $"{SecondOrder.StokesBoundAmplitude(c.HeightM, c.Wave.K, c.Wave.Kh):0.00000} m in the predicted elevation.");
        }

        double duration = req.NumWaves * req.PeriodS;
        var trajectory = TrajectoryGenerator.Generate(
            components, cfg.PaddleCentres(), angle, duration, req.SamplingHz, req.RampS, gain, x2);
        var eta = TrajectoryGenerator.PredictedElevation(components, duration, req.SamplingHz, req.RampS, gain);
        if (req.Order == WaveOrder.Second)
        {
            var c = components[0];
            double b = SecondOrder.StokesBoundAmplitude(c.HeightM, c.Wave.K, c.Wave.Kh);
            for (int i = 0; i < eta.Length; i++)
            {
                double t = i / req.SamplingHz, e = Ramp.Envelope(t, duration, req.RampS);
                eta[i] += gain * e * e * b * Math.Cos(2 * c.Wave.Omega * t);
            }
        }
        return new GeneratedWave(trajectory, eta, components, components) { Info = info, SecondOrderAmplitudeM = x2 };
    }

    public static WaveKinematics Kinematics(WaveComponent c)
    {
        var w = c.Wave;
        var regime = w.Kh < ShallowKh ? DepthRegime.Shallow : w.Kh > DeepKh ? DepthRegime.Deep : DepthRegime.Intermediate;
        double u = Math.PI * c.HeightM / w.PeriodS;
        return new WaveKinematics(w.K, w.Kh, w.WaveLengthM, w.CelerityMs, w.GroupVelocityMs, regime, u, u / Math.Tanh(w.Kh));
    }

    /// <summary>[VERIFY] force: linear dynamic amplitude ρ g A_w tanh(kh)/k over the full flume width (golden 177 N uses W = 3 m).</summary>
    private double Force(WaveComponent c) =>
        cfg.WaterDensity * cfg.Gravity * (c.HeightM / 2) * Math.Tanh(c.Wave.Kh) / c.Wave.K * cfg.FlumeWidthM;

    /// <summary>P = Σ (ρ g H² / 8) · Cg · W.</summary>
    private double Power(IEnumerable<WaveComponent> components) =>
        components.Sum(c => cfg.WaterDensity * cfg.Gravity * c.HeightM * c.HeightM / 8 * c.Wave.GroupVelocityMs * cfg.FlumeWidthM);

    public WavemakerOutputs Wavemaker(IReadOnlyList<WaveComponent> components, double gain = 1, double secondOrderAmpM = 0)
    {
        double xa = 0, v = 0, a = 0;
        foreach (var c in components)
        {
            double x = c.StrokeAmplitudeM, w = c.Wave.Omega;
            xa += x;
            v += x * w;
            a += x * w * w;
        }
        double x2 = Math.Abs(secondOrderAmpM), w1 = components[0].Wave.Omega;
        xa += x2;
        v += x2 * 2 * w1;
        a += x2 * 4 * w1 * w1;
        double power = Power(components) * gain * gain;
        return new WavemakerOutputs(components[0].C0, gain * xa, 2 * gain * xa, gain * v, gain * a,
            gain * components.Sum(Force), power, power * cfg.PaddleWidthM / cfg.FlumeWidthM);
    }

    private WavemakerOutputs FromTrajectory(GeneratedWave g, WorstValues worst, double gain)
    {
        double min = 0, max = 0;
        foreach (var row in g.Trajectory.PositionsM)
            foreach (var x in row) { min = Math.Min(min, x); max = Math.Max(max, x); }
        double? power = g.Spectrum.Count > 0 ? Power(g.Spectrum) * gain * gain : null;
        return new WavemakerOutputs(
            g.Design.Count > 0 ? g.Design[0].C0 : null,
            worst.MaxAbsPositionM, max - min, worst.MaxAbsVelocityMs, worst.MaxAbsAccelMs2,
            g.Design.Count > 0 ? gain * Force(g.Design[0]) : null,
            power, power * cfg.PaddleWidthM / cfg.FlumeWidthM);
    }
}
