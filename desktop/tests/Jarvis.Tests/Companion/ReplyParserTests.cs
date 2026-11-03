using Jarvis.Core.Companion;

namespace Jarvis.Tests.Companion;

public sealed class ReplyParserTests
{
    [Fact]
    public void EmitsEachSentenceAsSoonAsItEnds()
    {
        var parser = new ReplyParser();

        Assert.Empty(parser.Feed("It's the gear"));
        Assert.Equal([new ReplyItem.Line("It's the gear icon.")], parser.Feed(" icon. Click"));
        Assert.Empty(parser.Feed(" it."));
        Assert.Equal([new ReplyItem.Line("Click it.")], parser.Complete());
    }

    [Fact]
    public void PointTag_IsParsedAndNeverSpoken()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("It's up here. [POINT:1210,44:Settings:screen2] Click it.").Concat(parser.Complete()).ToList();

        Assert.Equal(
            [new ReplyItem.Point(1210, 44, "Settings", 2), new ReplyItem.Line("It's up here."), new ReplyItem.Line("Click it.")],
            items);
    }

    [Fact]
    public void TagSplitAcrossDeltas_IsHeldBackUntilComplete()
    {
        var parser = new ReplyParser();

        var first = parser.Feed("The red OK button is bottom right. [POINT:540");
        var second = parser.Feed(",305]");

        Assert.Equal([new ReplyItem.Line("The red OK button is bottom right.")], first);
        Assert.Equal([new ReplyItem.Point(540, 305, string.Empty, 1)], second);
        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void PointNone_IsDropped()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("Nothing to show here. [POINT:none]").Concat(parser.Complete()).ToList();

        Assert.Equal([new ReplyItem.Line("Nothing to show here.")], items);
    }

    [Fact]
    public void Decimals_AndAbbreviationsDoNotSplitMidNumber()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("Version 2.5 is out.").Concat(parser.Complete()).ToList();

        Assert.Equal([new ReplyItem.Line("Version 2.5 is out.")], items);
    }

    [Fact]
    public void LineBreaks_EndALine()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("First thing\nSecond thing").Concat(parser.Complete()).ToList();

        Assert.Equal([new ReplyItem.Line("First thing"), new ReplyItem.Line("Second thing")], items);
    }

    [Fact]
    public void RoutineBlock_IsParsedAndNeverSpoken()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("[ROUTINE:daily 08:00]Brief me").Concat(parser.Feed(" on the weather.[/ROUTINE] Every morning at eight.")).Concat(parser.Complete()).ToList();

        Assert.Equal([new ReplyItem.Recurring("daily 08:00", "Brief me on the weather."), new ReplyItem.Line("Every morning at eight.")], items);
    }

    [Fact]
    public void DriveSteps_AreParsedInOrder_AndNeverSpoken()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("Opening it. [DO:click 12,34:Save button:screen2] [DO:type:hi there] [DO:key:ctrl+s]").Concat(parser.Complete()).ToList();

        Assert.Equal(
            [
                new ReplyItem.Act(Jarvis.Contracts.Control.ComputerActionKind.Click, 12, 34, "Save button", 2, null),
                new ReplyItem.Act(Jarvis.Contracts.Control.ComputerActionKind.Type, 0, 0, string.Empty, 1, "hi there"),
                new ReplyItem.Act(Jarvis.Contracts.Control.ComputerActionKind.Key, 0, 0, string.Empty, 1, "ctrl+s"),
                new ReplyItem.Line("Opening it."),
            ],
            items);
    }

    [Fact]
    public void DriveDescriptions_SayExactlyWhatWillHappen()
    {
        Assert.Equal("Press Ctrl + Shift + S", DriveDescription.Of(new ReplyItem.Act(Jarvis.Contracts.Control.ComputerActionKind.Key, 0, 0, string.Empty, 1, "ctrl+shift+s")));
        Assert.Equal("Type \"hello\"", DriveDescription.Of(new ReplyItem.Act(Jarvis.Contracts.Control.ComputerActionKind.Type, 0, 0, string.Empty, 1, "hello")));
    }

    [Fact]
    public void OpenStep_KeepsTheWholeLink()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("Opening it. [DO:open:https://example.com/a?b=1]").Concat(parser.Complete()).ToList();

        Assert.Contains(new ReplyItem.Act(Jarvis.Contracts.Control.ComputerActionKind.Open, 0, 0, string.Empty, 1, "https://example.com/a?b=1"), items);
    }

    [Fact]
    public void ClickWithAColon_IsUnderstoodToo_AndNeverShown()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("Clicking it. [DO:click:567,340:Text box:screen1]").Concat(parser.Complete()).ToList();

        Assert.Equal([new ReplyItem.Act(Jarvis.Contracts.Control.ComputerActionKind.Click, 567, 340, "Text box", 1, null), new ReplyItem.Line("Clicking it.")], items);
    }
}
