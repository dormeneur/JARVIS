using System.Numerics;
using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Overlay;
using Jarvis.Core.Companion;
using Jarvis.Core.Drawing;

namespace Jarvis.Tests.Drawing;

public sealed class DrawingTests
{
    // A 1280x720 screenshot of a 2560x1440 display at x=1920: image pixels map 2:1 onto the desktop.
    private static readonly CapturedScreen Screen = new(new ScreenImage(new byte[] { 1 }, 1280, 720, HasCursor: true), 1920, 0, 2560, 1440);

    [Fact]
    public void Script_ParsesEveryCommand_AndSkipsJunk()
    {
        var script = DrawingScript.Parse("""
            ring 100 40 300 36 "Search box"
            box 10 20 30 40
            arrow 600 400 450 300 "Click here"
            num 100 200 1
            note 50 60 "Your files"
            explode 1 2 3
            box 1 2
            """, screen: 2);

        Assert.Equal(2, script.Screen);
        Assert.Equal(
            [
                new DrawCommand.Ring(100, 40, 300, 36, "Search box"),
                new DrawCommand.Box(10, 20, 30, 40, null),
                new DrawCommand.Arrow(600, 400, 450, 300, "Click here"),
                new DrawCommand.Step(100, 200, 1),
                new DrawCommand.Note(50, 60, "Your files"),
            ],
            script.Commands);
    }

    [Fact]
    public void ReplyParser_ExtractsDrawBlocks_AndNeverSpeaksThem()
    {
        var parser = new ReplyParser();

        var first = parser.Feed("Here are the steps. [DRAW:screen2]\nnum 10 20 1\nnum 30 40");
        var second = parser.Feed(" 2\n[/DRAW] Start with one.");
        var last = parser.Complete();

        Assert.Equal([new ReplyItem.Line("Here are the steps.")], first);
        var drawing = Assert.IsType<ReplyItem.Drawing>(Assert.Single(second));
        Assert.Equal(2, drawing.Script.Screen);
        Assert.Equal([new DrawCommand.Step(10, 20, 1), new DrawCommand.Step(30, 40, 2)], drawing.Script.Commands);
        Assert.Equal([new ReplyItem.Line("Start with one.")], last);
    }

    [Fact]
    public void ReplyParser_DropsAnUnclosedBlockAtTheEnd()
    {
        var parser = new ReplyParser();

        var items = parser.Feed("Look here. [DRAW]\nbox 1 2 3 4").Concat(parser.Complete()).ToList();

        Assert.Equal([new ReplyItem.Line("Look here.")], items);
    }

    [Fact]
    public void Scene_MapsToTheDesktop_AndDrawsStrokesInOrder()
    {
        var script = DrawingScript.Parse("box 100 100 200 50 \"Toolbar\"\narrow 640 600 640 400", screen: 1);

        var scene = SceneBuilder.Build(script, Screen, 0x3380FF, displayScale: 1.5f, seed: 7);

        // Box = 4 strokes, arrow = shaft + 2 head strokes.
        Assert.Equal(7, scene.Strokes.Count);
        Assert.All(scene.Strokes.Zip(scene.Strokes.Skip(1)), pair => Assert.True(pair.Second.Start >= pair.First.Start + pair.First.Duration));

        // The box's top edge starts near image (100,100) → desktop (1920+200, 200), minus padding.
        var firstPoint = scene.Strokes[0].Points[0].Position;
        Assert.InRange(firstPoint.X, 2100, 2125);
        Assert.InRange(firstPoint.Y, 180, 200);

        var label = Assert.Single(scene.Labels);
        Assert.Equal(("Toolbar", InkLabelStyle.Pill), (label.Text, label.Style));
        Assert.True(scene.Duration > TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public void FinishedStrokes_HaveFiniteOutlines()
    {
        // Regression: the last point's pressure used to be NaN, so finished strokes vanished on screen.
        var script = DrawingScript.Parse("box 100 100 200 50\nring 400 300 100 100\narrow 10 10 200 200", screen: 1);
        var scene = SceneBuilder.Build(script, Screen, 0, 1.25f, seed: 1);

        foreach (var stroke in scene.Strokes)
        {
            Assert.All(stroke.Points, point => Assert.True(float.IsFinite(point.Pressure)));
            Assert.All(StrokeOutline.Build(stroke.Points, stroke.Width, taper: stroke.Width * 2), point => Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y)));
        }
    }

    [Fact]
    public void LabelPill_LiftsAboveABadgeThatWouldCoverIt()
    {
        // Claude put badge 1 right where the box's label goes (seen live on the taskbar).
        var script = DrawingScript.Parse("box 350 686 216 28 \"Start and search\"\nnum 360 676 1", screen: 1);

        var scene = SceneBuilder.Build(script, Screen, 0, 1, seed: 1);

        var pill = scene.Labels.Single(label => label.Style == InkLabelStyle.Pill);
        var badge = scene.Labels.Single(label => label.Style == InkLabelStyle.Badge);
        Assert.True(pill.Anchor.Y <= badge.Anchor.Y - 24, $"pill at y={pill.Anchor.Y} should sit above the badge at y={badge.Anchor.Y}");
    }

    [Fact]
    public void Scene_IsReproducibleForASeed()
    {
        var script = DrawingScript.Parse("ring 100 100 200 50", screen: 1);

        var a = SceneBuilder.Build(script, Screen, 0, 1, seed: 3).Strokes[0].Points;
        var b = SceneBuilder.Build(script, Screen, 0, 1, seed: 3).Strokes[0].Points;

        Assert.Equal(a, b);
    }

    [Fact]
    public void Outline_IsAClosedShapeAroundThePath_AndPrefixShortensIt()
    {
        var path = Enumerable.Range(0, 21).Select(i => new InkPoint(new Vector2(i * 10, 0), 1)).ToList();

        var outline = StrokeOutline.Build(path, size: 8);
        var half = StrokeOutline.Prefix(path, 0.5f);

        Assert.All(outline, point => Assert.InRange(point.Y, -4.01f, 4.01f));
        Assert.InRange(outline.Max(point => point.X), 200, 204.01f);
        Assert.InRange(half[^1].Position.X, 99.9f, 100.1f);
    }
}
