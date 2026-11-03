using Jarvis.Core.Notch;

namespace Jarvis.Tests.Notch;

public sealed class NotchDwellTests
{
    private static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void RestingForTheHold_FillsTheRingInSteps_ThenOpensOnce()
    {
        var dwell = new NotchDwell();

        Assert.Equal((0.0, false), dwell.Update(At(10), onSliver: true, allowed: true));
        Assert.Equal((0.5, false), dwell.Update(At(10.225), onSliver: true, allowed: true));
        Assert.Equal((null, true), dwell.Update(At(10.45), onSliver: true, allowed: true));
        Assert.Equal((null, false), dwell.Update(At(20), onSliver: true, allowed: true)); // still there: no second time
    }

    [Fact]
    public void LeavingResets_AndRearms()
    {
        var dwell = new NotchDwell();
        dwell.Update(At(0), true, true);
        dwell.Update(At(0.4), false, true);

        Assert.Equal((0.0, false), dwell.Update(At(0.5), true, true)); // starts over
    }

    [Fact]
    public void NotAllowed_ShowsNoRing() => Assert.Equal((null, false), new NotchDwell().Update(At(0), true, allowed: false));

    [Theory]
    [InlineData(960, 2, true)]
    [InlineData(960 + 60, 30, true)]
    [InlineData(960 + 90, 10, false)]
    [InlineData(960, 60, false)]
    public void TheSliverAndAMarginAroundIt_Count(double x, double y, bool on)
        => Assert.Equal(on, NotchDwell.OnSliver(x, y, 0, 0, 1920, 1.25));
}
