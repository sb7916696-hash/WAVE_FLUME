using WaveFlume.Core;

namespace WaveFlume.Physics;

/// <summary>
/// Validates the full generated trajectory before anything is sent (spec §8).
/// The PLC must enforce the same limits independently — this is not the only line of defence.
/// </summary>
public sealed class SafetyGatekeeper(FlumeConfig cfg)
{
    public const double PhaseStepWarnRad = Math.PI / 2;
    public const double PhaseStepRejectRad = Math.PI;
    public const double MinSamplesPerPeriod = 10;
    public const double StartPositionToleranceM = 1e-6;
    public const double UrsellWarnLimit = 26;
    public const double MinKhWarn = 0.3;

    /// <summary>Regular waves: every component is both a design wave and a spectral component.</summary>
    public SafetyReport Validate(WaveRequest req, IReadOnlyList<WaveComponent> components, Trajectory traj) =>
        Validate(req, new GeneratedWave(traj, [], components, components), traj);

    /// <summary>
    /// Design waves (Hs/Tp, focus height, regular components) are checked for height range, breaking and
    /// applicability; spectral components for period range, sampling rate and the oblique phase step.
    /// Each issue code is reported once.
    /// </summary>
    public SafetyReport Validate(WaveRequest req, GeneratedWave g, Trajectory traj)
    {
        var v = new List<SafetyIssue>();
        var w = new List<SafetyIssue>();
        void Fail(SafetyIssue i) { if (v.All(x => x.Code != i.Code)) v.Add(i); }
        void Warn(SafetyIssue i) { if (w.All(x => x.Code != i.Code)) w.Add(i); }
        var r = cfg.Ranges;
        var l = cfg.Limits;

        // Inputs inside Settings ranges.
        var depth = cfg.EffectiveDepthRange;
        if (!depth.Contains(req.WaterDepthM))
            Fail(new("DEPTH_RANGE", $"Water depth {req.WaterDepthM} m outside [{depth.Min}, {depth.Max}] m."));
        if (g.UsesNumWaves && !r.NumWaves.Contains(req.NumWaves))
            Fail(new("NUM_WAVES_RANGE", $"Number of waves {req.NumWaves} outside [{r.NumWaves.Min}, {r.NumWaves.Max}]."));
        if (!r.SamplingHz.Contains(req.SamplingHz))
            Fail(new("FS_RANGE", $"Sampling frequency {req.SamplingHz} Hz outside [{r.SamplingHz.Min}, {r.SamplingHz.Max}] Hz."));
        foreach (var h in g.Heights ?? g.Design.Select(d => d.HeightM).ToList())
            if (!r.WaveHeightM.Contains(h))
                Fail(new("HEIGHT_RANGE", $"Wave height {h:0.####} m outside [{r.WaveHeightM.Min}, {r.WaveHeightM.Max}] m."));

        foreach (var c in g.Design)
        {
            double hBreak = LimitCurve.BreakingHeight(c.Wave);
            if (c.HeightM > hBreak)
                Fail(new("BREAKING", $"Wave height {c.HeightM:0.####} m exceeds breaking limit {hBreak:0.####} m at T={c.Wave.PeriodS:0.###} s."));
            bool longWaveTheory = req.Type is WaveType.Cnoidal or WaveType.Solitary;
            double ursell = c.HeightM * c.Wave.WaveLengthM * c.Wave.WaveLengthM / Math.Pow(req.WaterDepthM, 3);
            if (!longWaveTheory && ursell > UrsellWarnLimit)
                Warn(new("URSELL", $"Ursell number {ursell:0.#} > {UrsellWarnLimit}: linear/Stokes theory inaccurate, consider cnoidal."));
            if (!longWaveTheory && c.Wave.Kh < MinKhWarn)
                Warn(new("SHALLOW_KH", $"kh = {c.Wave.Kh:0.###} < {MinKhWarn}: consider cnoidal/solitary theory."));
        }

        double tMin = cfg.EffectiveTMinS, tMax = cfg.EffectiveTMaxS;

        // The requested period (Tp for a random sea) must itself be one the wavemaker can make. Without this check a
        // random sea with Tp outside the range passes, because its frequency band is simply cut to the allowed range.
        foreach (var c in g.Design)
        {
            double t = c.Wave.PeriodS;
            if (t < HardLimits.AbsoluteMinPeriodS)
                Fail(new("PERIOD_FLOOR", $"Period {t:0.###} s below absolute floor {HardLimits.AbsoluteMinPeriodS} s."));
            else if (t < tMin || t > tMax)
                Fail(new("PERIOD_RANGE", $"Period {t:0.###} s outside [{tMin}, {tMax}] s."));
        }

        foreach (var c in g.Spectrum)
        {
            double t = c.Wave.PeriodS;
            if (t < HardLimits.AbsoluteMinPeriodS)
                Fail(new("PERIOD_FLOOR", $"Period {t:0.###} s below absolute floor {HardLimits.AbsoluteMinPeriodS} s."));
            else if (t < tMin || t > tMax)
                Fail(new("PERIOD_RANGE", $"Period {t:0.###} s outside [{tMin}, {tMax}] s."));
            if (req.SamplingHz < MinSamplesPerPeriod * c.Wave.FrequencyHz)
                Fail(new("FS_TOO_LOW", $"Sampling frequency must be >= {MinSamplesPerPeriod} x {c.Wave.FrequencyHz:0.###} Hz."));
            double step = OrientationPhase.AdjacentStep(c.Wave.K, cfg.PaddleWidthM, Units.DegToRad(req.AngleDeg));
            if (step >= PhaseStepRejectRad)
                Fail(new("PHASE_STEP", $"Adjacent-paddle phase step {Units.RadToDeg(step):0.#} deg >= 180 deg at T={t:0.###} s (spatial aliasing)."));
            else if (step >= PhaseStepWarnRad)
                Warn(new("PHASE_STEP", $"Adjacent-paddle phase step {Units.RadToDeg(step):0.#} deg >= 90 deg at T={t:0.###} s."));
        }
        if (req.AngleDeg != 0)
            Warn(new("OBLIQUE_WALLS", "Oblique waves reflect from the flume side walls unless absorbers are fitted."));
        foreach (var x in g.Warnings) Warn(x);
        if (traj.SampleCount > cfg.Irregular.WarnSamplesPerPaddle)
            Warn(new("LARGE_TRAJECTORY", $"{traj.SampleCount:N0} samples per paddle: long upload and {traj.SampleCount * traj.PaddleCount * 4 / 1e6:0} MB in memory."));

        if (l.MaxAccelMs2 is null)
            Fail(new("ACCEL_NOT_CONFIGURED", "Acceleration limit not configured."));

        // Full-trajectory kinematics (all paddles, all terms, signal modification included).
        var worst = ScanTrajectory(traj);
        double clamp = Math.Min(l.MaxStrokeM, HardLimits.MaxStrokeCeilingM);
        double vMax = Math.Min(l.MaxVelocityMs, HardLimits.MaxVelocityCeilingMs);
        if (worst.MaxAbsPositionM > clamp)
            v.Add(new("STROKE", $"Paddle {worst.PositionPaddle + 1} position {worst.MaxAbsPositionM:0.####} m exceeds clamp ±{clamp} m."));
        if (worst.MaxAbsVelocityMs > vMax)
            v.Add(new("VELOCITY", $"Paddle {worst.VelocityPaddle + 1} velocity {worst.MaxAbsVelocityMs:0.###} m/s exceeds {vMax} m/s."));
        if (l.MaxAccelMs2 is { } aMax && worst.MaxAbsAccelMs2 > aMax)
            v.Add(new("ACCELERATION", $"Paddle {worst.AccelPaddle + 1} acceleration {worst.MaxAbsAccelMs2:0.###} m/s² exceeds {aMax} m/s²."));

        for (int p = 0; p < traj.PaddleCount; p++)
        {
            if (traj.SampleCount > 0 && Math.Abs(traj.PositionsM[p][0]) > StartPositionToleranceM)
            {
                v.Add(new("START_NOT_ZERO", $"Paddle {p + 1} trajectory does not start at X = 0."));
                break;
            }
        }

        return new SafetyReport(v, w, worst);
    }

    /// <summary>Max |X|, |dX/dt|, |d²X/dt²| by central differences.</summary>
    public static WorstValues ScanTrajectory(Trajectory traj)
    {
        double dt = 1 / traj.SampleRateHz;
        double xMax = 0, vMax = 0, aMax = 0;
        int xP = 0, vP = 0, aP = 0;
        for (int p = 0; p < traj.PaddleCount; p++)
        {
            var x = traj.PositionsM[p];
            for (int i = 0; i < x.Length; i++)
            {
                double ax = Math.Abs(x[i]);
                if (ax > xMax) { xMax = ax; xP = p; }
                if (i == 0 || i == x.Length - 1) continue;
                double vel = Math.Abs((x[i + 1] - (double)x[i - 1]) / (2 * dt));
                double acc = Math.Abs((x[i + 1] - 2.0 * x[i] + x[i - 1]) / (dt * dt));
                if (vel > vMax) { vMax = vel; vP = p; }
                if (acc > aMax) { aMax = acc; aP = p; }
            }
        }
        return new WorstValues(xMax, xP, vMax, vP, aMax, aP);
    }
}
