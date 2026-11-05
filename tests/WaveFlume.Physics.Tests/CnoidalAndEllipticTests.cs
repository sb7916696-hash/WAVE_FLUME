using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

/// <summary>
/// Verification of the elliptic functions and of cnoidal waves. The cnoidal tests check the solution against the
/// equation it was derived from (the integrated Korteweg-de Vries equation), not against the code that builds it.
/// </summary>
public class CnoidalAndEllipticTests
{
    private const double G = 9.81;
    private static readonly FlumeConfig Cfg = new() { Limits = new OperationalLimits { MaxAccelMs2 = 20 } };

    // ---- elliptic functions -----------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 1.5707963267948966)]
    [InlineData(0.5, 1.8540746773013719)]
    [InlineData(0.9, 2.5780921133481733)]
    public void Complete_elliptic_integral_of_the_first_kind_matches_published_values(double m, double expected) =>
        Elliptic.K(m).Should().BeApproximately(expected, 1e-12);

    [Theory]
    [InlineData(0.0, 1.5707963267948966)]
    [InlineData(0.5, 1.3506438810476755)]
    [InlineData(0.9, 1.1047747327040733)]
    public void Complete_elliptic_integral_of_the_second_kind_matches_published_values(double m, double expected) =>
        Elliptic.E(m).Should().BeApproximately(expected, 1e-12);

    [Theory]
    [InlineData(0.05)] [InlineData(0.3)] [InlineData(0.5)] [InlineData(0.8)] [InlineData(0.99)]
    public void Legendre_relation_holds(double m)
    {
        // E(m) K(1-m) + E(1-m) K(m) - K(m) K(1-m) = pi / 2
        double lhs = Elliptic.E(m) * Elliptic.K(1 - m) + Elliptic.E(1 - m) * Elliptic.K(m) - Elliptic.K(m) * Elliptic.K(1 - m);
        lhs.Should().BeApproximately(Math.PI / 2, 1e-12);
    }

    [Theory]
    [InlineData(0.1)] [InlineData(0.5)] [InlineData(0.9)] [InlineData(0.999)]
    public void Jacobi_cn_has_the_right_zeros_extremes_and_period(double m)
    {
        double k = Elliptic.K(m);
        Elliptic.Cn(0, m).Should().BeApproximately(1, 1e-12);
        Elliptic.Cn(k, m).Should().BeApproximately(0, 1e-9);
        Elliptic.Cn(2 * k, m).Should().BeApproximately(-1, 1e-9);
        for (double u = -3; u <= 3; u += 0.37)
            Elliptic.Cn(u + 4 * k, m).Should().BeApproximately(Elliptic.Cn(u, m), 1e-9, $"period 4K at u = {u}");
    }

    [Fact]
    public void Jacobi_cn_reduces_to_cosine_and_to_sech()
    {
        for (double u = -4; u <= 4; u += 0.4)
        {
            Elliptic.Cn(u, 0).Should().BeApproximately(Math.Cos(u), 1e-14);
            Elliptic.Cn(u, 1).Should().BeApproximately(1 / Math.Cosh(u), 1e-14);
            Elliptic.Cn(u, 1e-9).Should().BeApproximately(Math.Cos(u), 1e-8);
            Elliptic.Cn(u, 1 - 1e-12).Should().BeApproximately(1 / Math.Cosh(u), 1e-6);
        }
    }

    [Theory]
    [InlineData(0.2)] [InlineData(0.6)] [InlineData(0.95)]
    public void Jacobi_cn_satisfies_its_differential_equation(double m)
    {
        // d/du cn(u) = - sn(u) dn(u), with sn = sqrt(1 - cn^2) and dn = sqrt(1 - m sn^2) on 0 < u < 2K.
        double k = Elliptic.K(m), h = 1e-6;
        for (double u = 0.05 * k; u < 1.95 * k; u += 0.1 * k)
        {
            double cn = Elliptic.Cn(u, m), sn = Math.Sqrt(1 - cn * cn), dn = Math.Sqrt(1 - m * sn * sn);
            double slope = (Elliptic.Cn(u + h, m) - Elliptic.Cn(u - h, m)) / (2 * h);
            slope.Should().BeApproximately(-sn * dn, 1e-6, $"u = {u:0.000}");
        }
    }

    [Theory]
    [InlineData(0.3)] [InlineData(0.7)] [InlineData(0.95)]
    public void The_mean_of_cn_squared_over_a_period_is_the_closed_form(double m)
    {
        double k = Elliptic.K(m);
        int n = 200_000;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double c = Elliptic.Cn((i + 0.5) * 4 * k / n, m);
            sum += c * c;
        }
        (sum / n).Should().BeApproximately((Elliptic.E(m) / k - (1 - m)) / m, 1e-8);
    }

    // ---- cnoidal wave -----------------------------------------------------------------------

    public static IEnumerable<object[]> Cases() =>
    [
        [0.04, 4.0, 0.4], [0.06, 6.0, 0.6], [0.10, 5.0, 0.3], [0.03, 3.0, 0.2], [0.05, 8.0, 0.5], [0.12, 6.0, 0.8],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Solved_wave_has_the_requested_period_and_the_ursell_number_of_its_length(double height, double period, double depth)
    {
        var p = Dispatch(height, period, depth);
        (p.LengthM / p.CelerityMs).Should().BeApproximately(period, 1e-9 * period);
        p.Ursell.Should().BeApproximately(height * p.LengthM * p.LengthM / Math.Pow(depth, 3), 1e-9 * p.Ursell);
        p.M.Should().BeInRange(0.02, 1);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Mean_surface_level_is_zero_and_the_crest_is_the_trough_plus_the_height(double height, double period, double depth)
    {
        var p = Dispatch(height, period, depth);
        int n = 100_000;
        double sum = 0, max = double.MinValue, min = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            double cn = Elliptic.Cn((i + 0.5) * 4 * p.K / n, p.M);
            double eta = p.TroughM + height * cn * cn;
            sum += eta;
            max = Math.Max(max, eta);
            min = Math.Min(min, eta);
        }
        (sum / n).Should().BeApproximately(0, 1e-8 * height + 1e-9, "water is conserved: mean level = still water");
        (max - min).Should().BeApproximately(height, 1e-6 * height);
        min.Should().BeApproximately(p.TroughM, 1e-6 * height);
        p.TroughM.Should().BeNegative("the trough is below still water");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_solution_satisfies_the_integrated_kdv_equation(double height, double period, double depth)
    {
        // (c0 h^2 / 12) eta'^2 = -(c0 / 4h) (eta - e1)(eta - e2)(eta - e3), with the three roots of the cubic.
        var p = Dispatch(height, period, depth);
        double c0 = Math.Sqrt(G * depth);
        double e1 = p.TroughM + height, e2 = p.TroughM, e3 = e1 - height / p.M;
        double scale = 2 * p.K / p.LengthM, dx = 1e-5;
        double Eta(double x) { double c = Elliptic.Cn(scale * x, p.M); return p.TroughM + height * c * c; }

        for (double x = 0.03 * p.LengthM; x < 0.5 * p.LengthM; x += 0.07 * p.LengthM)
        {
            double slope = (Eta(x + dx) - Eta(x - dx)) / (2 * dx);
            double eta = Eta(x);
            double left = c0 * depth * depth / 12 * slope * slope;
            double right = -(c0 / (4 * depth)) * (eta - e1) * (eta - e2) * (eta - e3);
            left.Should().BeApproximately(right, 2e-5 * Math.Max(Math.Abs(right), 1e-6) + 1e-9, $"x = {x:0.00} m");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_speed_follows_from_the_sum_of_the_roots(double height, double period, double depth)
    {
        // c = c0 [1 + (e1 + e2 + e3) / (2h)]
        var p = Dispatch(height, period, depth);
        double c0 = Math.Sqrt(G * depth);
        double e1 = p.TroughM + height, e2 = p.TroughM, e3 = e1 - height / p.M;
        p.CelerityMs.Should().BeApproximately(c0 * (1 + (e1 + e2 + e3) / (2 * depth)), 1e-9 * c0);
    }

    [Fact]
    public void For_longer_periods_the_wave_speed_rises_towards_the_solitary_wave_speed_without_passing_it()
    {
        // m -> 1 gives c -> c0 (1 + H / 2h), the first-order form of sqrt(g (h + H)). The approach is only logarithmic
        // (E/K -> 0 like 1/ln), so a finite period is always a little slower than the solitary wave.
        const double h = 0.5, height = 0.05;
        double c0 = Math.Sqrt(G * h), solitary = c0 * (1 + height / (2 * h));
        double previous = 0;
        foreach (double t in new[] { 6.0, 8.0, 12.0, 16.0, 20.0 })
        {
            var p = Dispatch(height, t, h);
            p.CelerityMs.Should().BeGreaterThan(previous);
            p.CelerityMs.Should().BeLessThan(solitary);
            // exact relation at this m
            p.CelerityMs.Should().BeApproximately(c0 * (1 + height / (p.M * h) * (1 - p.M / 2 - 1.5 * p.E / p.K)), 1e-12 * c0);
            previous = p.CelerityMs;
        }
        Dispatch(height, 20, h).M.Should().BeGreaterThan(0.999999);
    }

    [Fact]
    public void A_longer_period_for_the_same_height_gives_a_larger_parameter_m()
    {
        double previous = 0;
        foreach (double t in new[] { 3.0, 4.0, 5.0, 7.0, 10.0, 20.0 })
        {
            var p = Dispatch(0.04, t, 0.4);
            p.M.Should().BeGreaterThan(previous);
            previous = p.M;
        }
    }

    [Fact]
    public void A_period_the_theory_cannot_give_is_refused_with_the_possible_range()
    {
        var act = () => CnoidalWave.Solve(0.05, 0.5, 0.5, G);
        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("cannot have a period").And.Contain("between");
    }

    // ---- through the engine -----------------------------------------------------------------

    private static CnoidalParameters Dispatch(double height, double period, double depth) =>
        CnoidalWave.Solve(height, period, depth, G);

    [Theory]
    [InlineData(0.04, 4.0, 0.4)] [InlineData(0.06, 6.0, 0.6)] [InlineData(0.03, 3.0, 0.2)]
    public void Cnoidal_motion_is_safe_starts_and_ends_at_rest_and_is_centred(double height, double period, double depth)
    {
        var req = new WaveRequest { Type = WaveType.Cnoidal, WaterDepthM = depth, WaveHeightM = height, PeriodS = period, NumWaves = 12, RampS = period };
        var r = new WaveMathEngine(Cfg).Calculate(req);
        r.Safety.Ok.Should().BeTrue(string.Join("; ", r.Safety.Violations.Select(v => v.Message)));
        var x = r.Trajectory.PositionsM[0];
        r.Trajectory.PaddleCount.Should().Be(6);
        x[0].Should().Be(0);
        Math.Abs(x[^1]).Should().BeLessThan(1e-5f);
        for (int p = 1; p < 6; p++) r.Trajectory.PositionsM[p].Should().Equal(x, "a straight wave: all paddles move together");

        // Centred: the middle of the record swings equally to both sides.
        int a = x.Length / 4, b = 3 * x.Length / 4;
        var mid = x[a..b];
        (mid.Max() + mid.Min()).Should().BeInRange(-0.35f * (mid.Max() - mid.Min()), 0.35f * (mid.Max() - mid.Min()));
        r.Info.Should().Contain(i => i.Contains("Cnoidal"));
    }

    [Theory]
    [InlineData(0.02, 6.0, 0.3)] [InlineData(0.03, 5.0, 0.25)]
    public void For_small_waves_the_cnoidal_stroke_matches_the_linear_long_wave_value(double height, double period, double depth)
    {
        // Long-wave limit of the piston transfer function: H / S0 = k h, so Xa = H / (2 k h).
        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest
        {
            Type = WaveType.Cnoidal, WaterDepthM = depth, WaveHeightM = height, PeriodS = period, NumWaves = 12, RampS = period,
        });
        var lin = Dispersion.Solve(period, depth, G);
        double xa = height / (2 * TransferFunctions.Piston(lin.Kh));
        r.Wavemaker.StrokeAmplitudeM.Should().BeApproximately(xa, 0.25 * xa, "same wave, two theories");
    }

    [Fact]
    public void Cnoidal_with_an_impossible_period_blocks_with_a_clear_message_and_a_hard_ceiling_remains()
    {
        var req = new WaveRequest { Type = WaveType.Cnoidal, WaveHeightM = 0.05, PeriodS = 0.6, WaterDepthM = 0.5, NumWaves = 12, RampS = 2 };
        new WaveMathEngine(Cfg).Invoking(e => e.Calculate(req)).Should().Throw<ArgumentException>().WithMessage("*cannot have a period*");

        // A wave as high as the water is deep is refused, either by the theory or by the breaking limit.
        var big = new WaveRequest { Type = WaveType.Cnoidal, WaveHeightM = 0.5, PeriodS = 6, WaterDepthM = 0.5, NumWaves = 12, RampS = 6 };
        bool refused;
        try { refused = !new WaveMathEngine(Cfg).Calculate(big).Safety.Ok; }
        catch (ArgumentException) { refused = true; }
        refused.Should().BeTrue("a wave this high for the depth breaks, whatever the theory");
    }

    [Fact]
    public void Cnoidal_does_not_suggest_cnoidal_theory_to_itself()
    {
        var r = new WaveMathEngine(Cfg).Calculate(new WaveRequest
        {
            Type = WaveType.Cnoidal, WaterDepthM = 0.4, WaveHeightM = 0.05, PeriodS = 6, NumWaves = 12, RampS = 6,
        });
        r.Safety.Warnings.Select(w => w.Code).Should().NotContain(["URSELL", "SHALLOW_KH"]);
    }
}
