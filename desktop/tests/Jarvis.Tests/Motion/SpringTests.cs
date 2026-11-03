using Jarvis.Core.Motion;

namespace Jarvis.Tests.Motion;

public sealed class SpringTests
{
    public static TheoryData<double> DampingFractions => [0.3, 0.6, 1.0, 1.5];

    [Theory]
    [MemberData(nameof(DampingFractions))]
    public void Step_ConvergesToTarget(double damping)
    {
        var spring = new Spring(0.3, damping);

        var (offset, velocity) = spring.Step(100, 0, 3);

        Assert.InRange(offset, -0.01, 0.01);
        Assert.InRange(velocity, -0.1, 0.1);
    }

    [Theory]
    [MemberData(nameof(DampingFractions))]
    public void Step_IsFrameRateIndependent(double damping)
    {
        var spring = new Spring(0.42, damping);

        var oneShot = spring.Step(250, -40, 0.5);
        var (offset, velocity) = (250.0, -40.0);
        for (var frame = 0; frame < 60; frame++)
        {
            (offset, velocity) = spring.Step(offset, velocity, 0.5 / 60);
        }

        Assert.Equal(oneShot.Offset, offset, 6);
        Assert.Equal(oneShot.Velocity, velocity, 6);
    }

    [Theory]
    [MemberData(nameof(DampingFractions))]
    public void Step_VelocityMatchesNumericalDerivative(double damping)
    {
        var spring = new Spring(0.4, damping);
        const double h = 1e-6;

        var (_, velocity) = spring.Step(80, 15, 0.2);
        var before = spring.Step(80, 15, 0.2 - h).Offset;
        var after = spring.Step(80, 15, 0.2 + h).Offset;

        Assert.Equal((after - before) / (2 * h), velocity, 3);
    }

    [Fact]
    public void Step_UnderdampedOvershoots_CriticalDoesNot()
    {
        static double MinimumOffset(Spring spring)
        {
            var minimum = double.MaxValue;
            for (var t = 0.0; t < 2; t += 0.005)
            {
                minimum = Math.Min(minimum, spring.Step(100, 0, t).Offset);
            }

            return minimum;
        }

        Assert.True(MinimumOffset(Springs.CursorFollow) < -1);
        Assert.True(MinimumOffset(Springs.NotchClose) >= 0);
    }

    [Fact]
    public void Step_ZeroElapsedChangesNothing()
    {
        Assert.Equal((42.0, 7.0), Springs.NotchOpen.Step(42, 7, 0));
        Assert.Equal((42.0, 7.0), Springs.NotchOpen.Step(42, 7, -1));
    }

    [Fact]
    public void CompositionPeriod_IsResponseOverTwoPi()
    {
        Assert.Equal(0.42 / (2 * Math.PI), Springs.NotchOpen.CompositionPeriod.TotalSeconds, 6);
    }

    [Fact]
    public void SpringPoint_SettlesOnTarget()
    {
        var point = new SpringPoint(0, 0);

        for (var frame = 0; frame < 120; frame++)
        {
            point.Advance(300, 200, 1.0 / 60, Springs.CursorFollow);
        }

        Assert.True(point.IsSettled(300, 200));
    }
}
