using Jarvis.Contracts.Notch;
using Jarvis.Core.Motion;
using Jarvis.Core.Notch;

namespace Jarvis.Tests.Notch;

public sealed class NotchLayoutTests
{
    [Fact]
    public void Idle_IsAFloatingSliver()
    {
        var frame = NotchLayout.FrameFor(NotchState.Idle);

        Assert.Equal((60.0, 8.0, true), (frame.Width, frame.Height, frame.IsSliver));
        Assert.Equal(Springs.NotchClose, frame.Spring);
    }

    [Fact]
    public void Listening_SnapsOpenWithoutASpring()
    {
        var frame = NotchLayout.FrameFor(new NotchState.Listening());

        Assert.Equal((360.0, 32.0, false), (frame.Width, frame.Height, frame.IsSliver));
        Assert.Null(frame.Spring);
    }

    [Theory]
    [InlineData("", 70)]
    [InlineData("Short answer.", 142)]
    [InlineData("one\ntwo\nthree\nfour\nfive", 178)]
    public void Response_HeightGrowsWithLines(string text, double expectedHeight)
    {
        var frame = NotchLayout.FrameFor(new NotchState.Response(text));

        Assert.Equal(532, frame.Width);
        Assert.Equal(expectedHeight, frame.Height);
    }

    [Fact]
    public void Response_HeightIsCapped()
    {
        var frame = NotchLayout.FrameFor(new NotchState.Response(new string('x', 5000)));

        Assert.Equal(NotchLayout.MaxExpandedHeight, frame.Height);
    }

    [Fact]
    public void EffectiveCornerRadius_KeepsShortShapesPillShaped()
    {
        Assert.Equal(4, NotchLayout.EffectiveCornerRadius(NotchLayout.FrameFor(NotchState.Idle)), 9);
        Assert.Equal(0.854 * 16, NotchLayout.EffectiveCornerRadius(NotchLayout.FrameFor(new NotchState.Thinking())), 9);
        Assert.Equal(0.854 * 22, NotchLayout.EffectiveCornerRadius(NotchLayout.FrameFor(new NotchState.Response("hi"))), 9);
    }

    [Fact]
    public void Accent_OnlyLightsActiveStates()
    {
        Assert.Null(NotchAccent.GlowFor(NotchState.Idle));
        Assert.Equal(3, NotchAccent.GlowFor(new NotchState.Thinking())!.Count);
    }

    [Fact]
    public void Accent_OnlyLiveStatesMove()
    {
        Assert.True(NotchAccent.GlowMoves(new NotchState.Listening()));
        Assert.False(NotchAccent.GlowMoves(new NotchState.Response("long answer")));
    }

    [Fact]
    public void ResponseWithButtons_GrowsByTheButtonRow()
    {
        var plain = NotchLayout.FrameFor(new NotchState.Response("Done."));
        var withButtons = NotchLayout.FrameFor(new NotchState.Response("Done.", [new("Open", () => { })]));

        Assert.Equal(plain.Height + NotchLayout.ActionBarHeight, withButtons.Height);
        Assert.Equal(NotchLayout.ExpandedWidth, withButtons.Width); // a few buttons: the usual width
    }

    [Fact]
    public void HomeWithFiveTiles_WidensSoAllFitInOneRow()
    {
        string[] titles = ["Ask Jarvis", "Fill a form", "Private: off", "Hunches", "Settings"];
        var home = new NotchState.Home("Evening.", "Hold Ctrl + Alt…", [.. titles.Select(t => new HomeItem("x", t, null, () => { }))], [], () => { });
        var frame = NotchLayout.FrameFor(home);

        var needed = titles.Sum(NotchLayout.HomePillWidth) + (NotchLayout.HomePillSpacing * 4) + NotchLayout.PanelChrome;
        Assert.True(frame.Width >= needed, $"{frame.Width} < {needed}: the first and last tiles would be cut off");
        Assert.Equal(NotchLayout.HomeHeight, frame.Height); // still one row
    }

    [Fact]
    public void HunchCard_FitsEveryButton_WrappingWhatTheWidestPanelCannotHold()
    {
        string[] labels = ["Yes, JARVIS is my sidekick.", "Sort of, but mostly for fun", "No, I'm just learning", "In my words", "Why?", "Skip"];
        var card = new NotchState.Response("I have a hunch… you code late to avoid distractions?", [.. labels.Select(l => new NotchAction(l, () => { }))]);
        var frame = NotchLayout.FrameFor(card);

        var widths = labels.Select(NotchLayout.ActionWidth).ToList();
        var rows = NotchLayout.Rows(widths, NotchLayout.ActionSpacing, frame.Width - NotchLayout.PanelChrome);
        Assert.Equal(NotchLayout.MaxPanelWidth, frame.Width);
        Assert.Equal(2, rows.Count);
        Assert.Equal(labels.Length, rows.Sum());
        var at = 0;
        foreach (var count in rows) // no row is wider than the panel's room
        {
            Assert.True(widths.Skip(at).Take(count).Sum() + (NotchLayout.ActionSpacing * (count - 1)) <= frame.Width - NotchLayout.PanelChrome);
            at += count;
        }
    }

    [Fact]
    public void Rows_FillEachRowFirst_AndGiveAnOversizedItemItsOwn()
    {
        Assert.Equal([2, 1], NotchLayout.Rows([40, 40, 40], 10, 100));
        Assert.Equal([1, 1], NotchLayout.Rows([500, 20], 10, 100));
        Assert.Empty(NotchLayout.Rows([], 10, 100));
    }
}
