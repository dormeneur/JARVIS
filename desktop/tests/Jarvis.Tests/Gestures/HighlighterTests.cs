using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Overlay;
using Jarvis.Core.Companion;
using Jarvis.Core.Gestures;

namespace Jarvis.Tests.Gestures;

public sealed class HighlighterTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(15);

    [Fact]
    public void Circling_GivesTheCircledArea()
    {
        var highlighter = new Highlighter();
        var now = TimeSpan.Zero;
        highlighter.Start(500, 500, 1.0, now);
        for (var angle = 0; angle <= 360; angle += 10)
        {
            now += Frame;
            highlighter.Add(500 + (50 * Math.Cos(angle * Math.PI / 180)), 500 + (30 * Math.Sin(angle * Math.PI / 180)), now);
        }

        Assert.True(highlighter.IsDrawing);
        var area = highlighter.Finish()!;
        Assert.False(area.IsPoint);
        Assert.Equal(450, area.Left, 0.5);
        Assert.Equal(550, area.Right, 0.5);
        Assert.Equal(470, area.Top, 0.5);
    }

    [Fact]
    public void SmallJitter_IsNotADrawing_ButRestingIsAPoint()
    {
        var highlighter = new Highlighter();
        highlighter.Start(300, 300, 1.25, TimeSpan.Zero);
        highlighter.Add(303, 302, TimeSpan.FromSeconds(0.2));
        highlighter.Add(302, 303, TimeSpan.FromSeconds(0.5));

        Assert.False(highlighter.IsDrawing);
        Assert.Equal(new ScreenHighlight(300, 300, 300, 300, IsPoint: true), highlighter.Finish());
    }

    [Fact]
    public void QuickTalkWithoutMoving_MarksNothing()
    {
        var highlighter = new Highlighter();
        highlighter.Start(300, 300, 1.0, TimeSpan.Zero);
        highlighter.Add(300, 300, TimeSpan.FromSeconds(0.1));

        Assert.Null(highlighter.Finish());
    }

    [Fact]
    public void Description_IsInTheScreenshotsPixels_OnTheRightScreen()
    {
        // Screen 2: a 2000x1000 display at x=1920 sent as a 1000x500 image.
        IReadOnlyList<CapturedScreen> screens =
        [
            new(new ScreenImage(new byte[] { 1 }, 1280, 720, true), 0, 0, 1920, 1080),
            new(new ScreenImage(new byte[] { 2 }, 1000, 500, false), 1920, 0, 2000, 1000),
        ];

        var text = CompanionPrompt.DescribeHighlight(new ScreenHighlight(2120, 100, 2320, 300, IsPoint: false), screens);

        Assert.Contains("from (100,50) to (200,150) on screen 2", text, StringComparison.Ordinal);
    }
}
