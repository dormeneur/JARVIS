using Jarvis.Core.Motion;

namespace Jarvis.Tests.Motion;

public sealed class FlightPathTests
{
    [Fact]
    public void PoseAt_StartsAndLandsOnEndpoints()
    {
        var path = new FlightPath(100, 500, 900, 300);

        var start = path.PoseAt(TimeSpan.Zero);
        var end = path.PoseAt(path.Duration);

        Assert.Equal((100.0, 500.0), (start.X, start.Y));
        Assert.Equal(900, end.X, 9);
        Assert.Equal(300, end.Y, 9);
        Assert.Equal(1, end.Scale, 9);
    }

    [Theory]
    [InlineData(100, 0.6)]
    [InlineData(800, 1.0)]
    [InlineData(5000, 1.4)]
    public void Duration_ScalesWithDistanceWithinLimits(double distance, double expectedSeconds)
    {
        var path = new FlightPath(0, 0, distance, 0);

        Assert.Equal(expectedSeconds, path.Duration.TotalSeconds, 9);
    }

    [Fact]
    public void PoseAt_ArcsAboveTheStraightLine()
    {
        var path = new FlightPath(0, 500, 1000, 500);

        var middle = path.PoseAt(path.Duration / 2);

        Assert.True(middle.Y < 500 - 30, $"expected an upward arc, got y={middle.Y}");
        Assert.Equal(1.3, middle.Scale, 9);
    }

    [Fact]
    public void PoseAt_RotationFollowsDirectionOfTravel()
    {
        var rightward = new FlightPath(0, 0, 1000, 0).PoseAt(TimeSpan.FromSeconds(0.6));

        Assert.InRange(rightward.RotationDegrees, 80, 100);
    }
}
