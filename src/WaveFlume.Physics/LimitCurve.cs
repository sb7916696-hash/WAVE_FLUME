using WaveFlume.Core;

namespace WaveFlume.Physics;

public enum LimitKind { Breaking, HeightCap, Stroke, Velocity, Acceleration }

public readonly record struct LimitPoint(double PeriodS, double HeightM, LimitKind Active);

/// <summary>Operating-limit curve H_lim(T) (spec §7.11).</summary>
public sealed class LimitCurve(FlumeConfig cfg)
{
    /// <summary>Miche breaking coefficient.</summary>
    public const double MicheCoefficient = 0.142;

    public static double BreakingHeight(DispersionResult wave) =>
        MicheCoefficient * wave.WaveLengthM * Math.Tanh(wave.Kh);

    public LimitPoint Evaluate(double periodS, double depthM)
    {
        var wave = Dispersion.Solve(periodS, depthM, cfg.Gravity);
        double c0 = TransferFunctions.C0(cfg.PaddleType, wave.K, depthM, cfg.HingeHeightM);
        double w = wave.Omega;
        var l = cfg.Limits;
        double stroke = Math.Min(l.MaxStrokeM, HardLimits.MaxStrokeCeilingM);
        double vel = Math.Min(l.MaxVelocityMs, HardLimits.MaxVelocityCeilingMs);

        var candidates = new (double H, LimitKind Kind)[]
        {
            (BreakingHeight(wave), LimitKind.Breaking),
            (cfg.Ranges.WaveHeightM.Max, LimitKind.HeightCap),
            (2 * c0 * stroke, LimitKind.Stroke),
            (2 * c0 * vel / w, LimitKind.Velocity),
            // Unconfigured a_max imposes no curve; the gatekeeper blocks sending instead.
            (l.MaxAccelMs2 is { } a ? 2 * c0 * a / (w * w) : double.PositiveInfinity, LimitKind.Acceleration),
        };
        var min = candidates.MinBy(c => c.H);
        return new LimitPoint(periodS, min.H, min.Kind);
    }

    public IReadOnlyList<LimitPoint> Compute(double depthM, int points = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(points, 2);
        double t0 = cfg.EffectiveTMinS, t1 = cfg.EffectiveTMaxS;
        return Enumerable.Range(0, points)
            .Select(i => Evaluate(t0 + (t1 - t0) * i / (points - 1), depthM))
            .ToList();
    }

    public bool IsInside(double heightM, double periodS, double depthM) =>
        periodS >= cfg.EffectiveTMinS && periodS <= cfg.EffectiveTMaxS &&
        heightM <= Evaluate(periodS, depthM).HeightM;
}
