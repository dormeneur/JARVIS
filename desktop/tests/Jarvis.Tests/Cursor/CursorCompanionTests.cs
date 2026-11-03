using Jarvis.Core.Cursor;

namespace Jarvis.Tests.Cursor;

public sealed class CursorCompanionTests
{
    private const double Scale = 1.25;
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1.0 / 60);

    private TimeSpan _now;

    [Fact]
    public void FirstFrame_SitsAtTheFollowOffset()
    {
        var pose = new CursorCompanion().Update(_now, 1000, 500, Scale);

        Assert.Equal(1000 + (35 * Scale), pose.X);
        Assert.Equal(500 + (25 * Scale), pose.Y);
        Assert.Equal(-35, pose.RotationDegrees);
    }

    [Fact]
    public void MouseMove_IsTrailedThenSettles()
    {
        var companion = new CursorCompanion();
        Run(companion, 1, 0, 0);

        var early = Step(companion, 600, 400);
        var settled = Run(companion, 90, 600, 400);

        Assert.True(early.X < 600, "the companion lags behind a sudden move");
        Assert.Equal(600 + (35 * Scale), settled.X, tolerance: 0.5);
        Assert.True(settled.IsSettled);
    }

    [Fact]
    public void PointAt_FliesToTarget_ThenReturnsWhenMouseMovesAway()
    {
        var companion = new CursorCompanion();
        Run(companion, 1, 100, 100);

        companion.PointAt(1500, 900);
        var midFlight = Run(companion, 20, 100, 100);
        var landed = Run(companion, 80, 100, 100);

        Assert.True(midFlight.Scale > 1, "the cursor swells mid-flight");
        Assert.Equal(1500 + (8 * Scale), landed.X, 6);
        Assert.Equal(900 + (12 * Scale), landed.Y, 6);

        var stillPointing = Run(companion, 5, 150, 100);
        Assert.Equal(landed.X, stillPointing.X, 6);

        var returning = Run(companion, 90, 400, 100);
        Assert.Equal(400 + (35 * Scale), returning.X, tolerance: 0.5);
    }

    [Fact]
    public void Shake_Detaches_ThenReattachesAfterThreeSeconds()
    {
        var companion = new CursorCompanion();
        Run(companion, 1, 500, 500);

        for (var i = 0; i < 16 && !companion.IsDetached; i++)
        {
            Step(companion, i % 2 == 0 ? 600 : 400, 500);
        }

        Assert.True(companion.IsDetached);
        var frozen = Step(companion, 900, 900);
        Assert.Equal(frozen.X, Step(companion, 100, 100).X);

        Run(companion, 185, 100, 100);
        Assert.False(companion.IsDetached);
    }

    [Fact]
    public void Shake_IsIgnoredWhenDisabledOrThinking()
    {
        var disabled = new CursorCompanion { ShakeToDetachEnabled = false };
        var thinking = new CursorCompanion();
        thinking.SetThinking(true, _now);

        foreach (var companion in new[] { disabled, thinking })
        {
            for (var i = 0; i < 30; i++)
            {
                Step(companion, i % 2 == 0 ? 600 : 400, 500);
            }

            Assert.False(companion.IsDetached);
        }
    }

    [Fact]
    public void Thinking_ReportsElapsedTime_AndIsNeverSettled()
    {
        var companion = new CursorCompanion();
        companion.SetThinking(true, _now);

        var pose = Run(companion, 30, 0, 0);

        Assert.NotNull(pose.ThinkingSeconds);
        Assert.InRange(pose.ThinkingSeconds!.Value, 0.45, 0.55);
        Assert.False(pose.IsSettled);

        companion.SetThinking(false, _now);
        Assert.Null(Step(companion, 0, 0).ThinkingSeconds);
    }

    [Fact]
    public void ThinkingDots_StayOnTheTriangleCorners()
    {
        for (var t = 0.0; t < 2.16; t += 0.05)
        {
            var dot = ThinkingDots.Pose(0, t);
            var distance = Math.Sqrt((dot.X * dot.X) + (dot.Y * dot.Y));
            Assert.InRange(distance, ThinkingDots.CornerDistance / 2, ThinkingDots.CornerDistance + 0.001);
        }
    }

    [Theory]
    [InlineData("#3380FF", CursorColor.Blue)]
    [InlineData("green", CursorColor.Green)]
    [InlineData("ff4c4c", CursorColor.Red)]
    [InlineData("nonsense", CursorColor.Blue)]
    [InlineData(null, CursorColor.Blue)]
    public void CursorColor_ParsesNamesAndHex(string? value, uint expected)
        => Assert.Equal(expected, CursorColor.Parse(value));

    [Fact]
    public void ListeningMeter_JumpsWithTheVoice_ThenFallsBack_AndKeepsFrames()
    {
        var companion = new CursorCompanion();
        companion.ShowLevel(0.9); // not listening yet: ignored
        Assert.Null(Step(companion, 0, 0).ListeningLevel);

        companion.SetListening(true);
        companion.ShowLevel(0.8);
        var loud = Step(companion, 0, 0);
        Assert.Equal(0.8, loud.ListeningLevel);
        Assert.False(loud.IsSettled); // the meter needs frames even when the mouse is still

        companion.ShowLevel(0);
        var falling = Run(companion, 6, 0, 0).ListeningLevel!.Value; // 0.1 s later
        Assert.InRange(falling, 0.1, 0.5);

        companion.SetListening(false);
        Assert.Null(Step(companion, 0, 0).ListeningLevel);
    }

    [Fact]
    public void ListeningBars_GrowWithLevel_MiddleTallest()
    {
        Assert.Equal(ListeningBars.Height(0, 0), ListeningBars.Height(2, 0));
        Assert.True(ListeningBars.Height(2, 1) > ListeningBars.Height(1, 1));
        Assert.True(ListeningBars.Height(1, 1) > ListeningBars.Height(0, 1));
    }

    [Fact]
    public void ClickedDetachedCursor_StaysWhileListening_ThenReattaches()
    {
        var companion = DetachedCompanion();
        companion.Clicked(_now);
        companion.SetListening(true);

        Run(companion, 600, 0, 0); // 10 s of talking
        Assert.Equal(CursorPhase.Detached, companion.Phase);

        companion.SetListening(false);
        Step(companion, 0, 0);
        Assert.Equal(CursorPhase.Following, companion.Phase);
    }

    [Fact]
    public void Bubble_ShowsTheLabelOnArrival_AndAPhraseWithoutOne()
    {
        var companion = new CursorCompanion();
        Run(companion, 1, 100, 100);
        companion.PointAt(900, 500, "Settings");

        Assert.Null(Step(companion, 100, 100).Bubble); // still flying
        Assert.Equal("Settings", Run(companion, 120, 100, 100).Bubble);

        companion.PointAt(300, 300);
        Assert.Equal("over here", Run(companion, 120, 100, 100).Bubble);
    }

    /// <summary>A companion that a mouse shake has just left behind.</summary>
    private CursorCompanion DetachedCompanion()
    {
        var companion = new CursorCompanion();
        Run(companion, 1, 500, 500);
        for (var i = 0; i < 16 && !companion.IsDetached; i++)
        {
            Step(companion, i % 2 == 0 ? 600 : 400, 500);
        }

        Assert.True(companion.IsDetached);
        return companion;
    }

    private CursorPose Step(CursorCompanion companion, double x, double y)
    {
        _now += Frame;
        return companion.Update(_now, x, y, Scale);
    }

    private CursorPose Run(CursorCompanion companion, int frames, double x, double y)
    {
        var pose = default(CursorPose);
        for (var i = 0; i < frames; i++)
        {
            pose = Step(companion, x, y);
        }

        return pose;
    }
}
