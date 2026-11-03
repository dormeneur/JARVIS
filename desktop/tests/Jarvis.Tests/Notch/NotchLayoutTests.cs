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
    }
}
