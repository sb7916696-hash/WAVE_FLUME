using FluentAssertions;
using WaveFlume.Core;
using WaveFlume.Physics;

namespace WaveFlume.Physics.Tests;

public class HydrodynamicsTests
{
    public static IEnumerable<object[]> PeriodDepthGrid() =>
        from t in new[] { 0.3, 0.5, 1.0, 2.0, 4.0, 6.0 }
        from h in new[] { 0.2, 0.4, 0.6, 0.8, 1.0 }
        select new object[] { t, h };

    [Theory]
    [MemberData(nameof(PeriodDepthGrid))]
    public void Dispersion_converges_and_satisfies_relation(double t, double h)
    {
        var w = Dispersion.Solve(t, h, 9.81);
        double residual = 9.81 * w.K * Math.Tanh(w.Kh) - w.Omega * w.Omega;
        residual.Should().BeApproximately(0, 1e-9);
        w.CelerityMs.Should().BeApproximately(w.WaveLengthM / t, 1e-9);
        w.GroupFactorN.Should().BeInRange(0.5, 1.0);
    }

    [Fact]
    public void Dispersion_rejects_bad_input()
    {
        FluentActions.Invoking(() => Dispersion.Solve(0, 0.6, 9.81)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => Dispersion.Solve(1, -1, 9.81)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void Flap_equals_elevated_hinge_at_zero_and_closed_form(double kh)
    {
        const double h = 0.6;
        double k = kh / h;
        double closed = 4 * Math.Sinh(kh) * (kh * Math.Sinh(kh) - Math.Cosh(kh) + 1) / (kh * (Math.Sinh(2 * kh) + 2 * kh));
        TransferFunctions.Flap(k, h).Should().BeApproximately(closed, 1e-12);
        TransferFunctions.ElevatedHinge(k, h, 0).Should().Be(TransferFunctions.Flap(k, h));
    }

    [Fact]
    public void Piston_limits()
    {
        TransferFunctions.Piston(6).Should().BeGreaterThan(1.99);
        TransferFunctions.Piston(1e-3).Should().BeApproximately(1e-3, 1e-6);
        // Alternate form 2 sinh² / (sinh cosh + kh).
        double kh = 1.3, s = Math.Sinh(kh);
        TransferFunctions.Piston(kh).Should().BeApproximately(2 * s * s / (s * Math.Cosh(kh) + kh), 1e-12);
    }

    [Fact]
    public void Transfer_functions_stay_finite_in_very_deep_water()
    {
        TransferFunctions.Piston(1000).Should().Be(2);
        TransferFunctions.Flap(1000 / 0.6, 0.6).Should().BeApproximately(2 * (1 - 1.0 / 1000), 1e-9);
        TransferFunctions.ElevatedHinge(1000 / 0.6, 0.6, 0.2).Should().BeInRange(1.9, 2.0);
    }

    [Theory]
    [InlineData(0.5, 0.2)]
    [InlineData(2.0, 0.4)]
    public void Elevated_hinge_matches_spec_formula(double kh, double h0)
    {
        const double h = 0.6;
        double k = kh / h, sh = Math.Sinh(kh), ch = Math.Cosh(kh);
        double spec = 2 * sh * ((h - h0) * k * sh - ch + Math.Cosh(k * h0)) / (k * (h - h0) * (sh * ch + kh));
        TransferFunctions.ElevatedHinge(k, h, h0).Should().BeApproximately(spec, 1e-12);
    }

    [Fact]
    public void Hinge_must_be_below_depth()
    {
        FluentActions.Invoking(() => TransferFunctions.ElevatedHinge(4, 0.6, 0.6)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ramp_endpoints_and_envelope()
    {
        Ramp.Up(0, 2).Should().Be(0);
        Ramp.Up(1, 2).Should().BeApproximately(0.5, 1e-12);
        Ramp.Up(2, 2).Should().Be(1);
        Ramp.Envelope(10, 10, 2).Should().Be(0);
        Ramp.Envelope(5, 10, 2).Should().Be(1);
        Ramp.Up(5, 0).Should().Be(1);
    }

    [Fact]
    public void Trajectory_starts_and_ends_at_rest()
    {
        var cfg = new FlumeConfig();
        var c = WaveComponent.Create(0.05, 1.0, 0.6, cfg);
        var traj = TrajectoryGenerator.Generate([c], cfg.PaddleCentres(), Units.DegToRad(20), 10, 125, 2, 1);
        foreach (var x in traj.PositionsM)
        {
            x[0].Should().Be(0);
            x[^1].Should().BeApproximately(0, 1e-7f);
            Math.Abs(x[1] - x[0]).Should().BeLessThan(1e-6f, "velocity is continuous (zero) at start");
        }
    }

    [Fact]
    public void Ramp_longer_than_half_duration_is_rejected()
    {
        var cfg = new FlumeConfig();
        var c = WaveComponent.Create(0.05, 1.0, 0.6, cfg);
        FluentActions.Invoking(() => TrajectoryGenerator.Generate([c], cfg.PaddleCentres(), 0, 10, 125, 6, 1))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Per_paddle_phase_follows_formula()
    {
        var cfg = new FlumeConfig();
        const double theta = 15;
        var c = WaveComponent.Create(0.05, 1.0, 0.6, cfg);
        var y = cfg.PaddleCentres();
        y.Should().Equal(0.25, 0.75, 1.25, 1.75, 2.25, 2.75);

        // No ramp → X_p(t) = X_a sin(ωt + φ_p); recover φ_p from samples 0 and quarter-period.
        var traj = TrajectoryGenerator.Generate([c], y, Units.DegToRad(theta), 10, 1000, 0, 1);
        int quarter = 250; // T/4 at 1 kHz
        for (int p = 0; p < 6; p++)
        {
            double expected = OrientationPhase.PaddlePhase(c.Wave.K, y[p], Units.DegToRad(theta));
            double sinPhi = traj.PositionsM[p][0] / c.StrokeAmplitudeM;
            double cosPhi = traj.PositionsM[p][quarter] / c.StrokeAmplitudeM;
            Math.Atan2(sinPhi, cosPhi).Should().BeApproximately(Math.IEEERemainder(expected, 2 * Math.PI), 1e-5);
        }
    }

    [Fact]
    public void Drive_geometry_and_motor_count_round_trip()
    {
        var drive = new DriveConfig();
        drive.MmPerPinionRev.Should().BeApproximately(314.159, 1e-3);
        drive.MmPerMotorRev.Should().BeApproximately(15.708, 1e-3);
        foreach (double mm in new[] { -350.0, -0.1, 0, 12.345, 350 })
        {
            long counts = Units.MmToMotorCounts(mm, 131_072, drive);
            Units.MotorCountsToMm(counts, 131_072, drive).Should().BeApproximately(mm, drive.MmPerMotorRev / 131_072);
        }
    }
}
