using System.Numerics;
using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Overlay;
using Jarvis.Core.Drawing;

namespace Jarvis.Tests.Drawing;

public sealed class FigureTests
{
    private static readonly CapturedScreen Screen = new(new ScreenImage(new byte[] { 1 }, 1280, 720, HasCursor: true), 1920, 0, 2560, 1440);

    [Fact]
    public void Script_ParsesFiguresAndSolids_ColourBeforeLabel()
    {
        var script = DrawingScript.Parse("""
            figure 600 300 200 "Water"
            ball 0 0.1 0 0.4 red "Oxygen"
            block 0 0 0 1 2 0.5 Base
            cone 0 0 0 0.5 1 #00ff00
            rod 0 0 0 1 1 1 0.1
            ball 9 9 9 -2
            """, screen: 1);

        Assert.Equal(
            [
                new DrawCommand.Figure(600, 300, 200, "Water"),
                new DrawCommand.Solid(FigureShape.Sphere, new(0, 0.1f, 0), new(0, 0.1f, 0), 0.4f, 0xFF4C4C, "Oxygen"),
                new DrawCommand.Solid(FigureShape.Box, new(-0.5f, -1, -0.25f), new(0.5f, 1, 0.25f), 0, null, "Base"),
                new DrawCommand.Solid(FigureShape.Cone, new(0, -0.5f, 0), new(0, 0.5f, 0), 0.5f, 0x00FF00, null),
                new DrawCommand.Solid(FigureShape.Cylinder, Vector3.Zero, Vector3.One, 0.1f, null, null),
                new DrawCommand.Solid(FigureShape.Sphere, new(1.5f), new(1.5f), 2, null, null), // clamped, not dropped
            ],
            script.Commands);
    }

    [Fact]
    public void SceneBuilder_GivesPartsToTheFigureBeforeThem_InkByDefault_AndDropsStrayOnes()
    {
        var script = DrawingScript.Parse("""
            ball 0 0 0 1
            figure 640 360 100 "Thing"
            ball 0 0 0 0.5
            block 0 0 0 1 1 1 green
            figure 100 100 50
            """, screen: 1);

        var drawing = SceneBuilder.Build(script, Screen, 0x123456, 2, seed: 1);

        var figure = Assert.Single(drawing.Figures); // the empty second figure is dropped
        Assert.Equal(new Vector2(1920 + 1280, 720), figure.Centre);
        Assert.Equal(200, figure.Size); // screenshot pixels map 2:1
        Assert.Equal([0x123456u, 0x26C281u], figure.Parts.Select(part => part.Rgb));
        Assert.True(drawing.Duration >= ScreenDrawing.FigureGrow);
    }

    [Fact]
    public void Project_ShowsOnlyTheFacesTurnedToTheViewer_BackToFront()
    {
        var cube = new InkFigure(new Vector2(500, 500), 200, [new FigurePart(FigureShape.Box, new(-0.5f), new(0.5f), 0, 0xFFFFFF, "Cube")], "Title", TimeSpan.FromSeconds(1));

        Assert.Empty(Figure3D.Project(cube, TimeSpan.FromSeconds(0.5), 1).Faces); // not started yet

        for (var second = 2.0; second < 16; second += 0.7)
        {
            var frame = Figure3D.Project(cube, TimeSpan.FromSeconds(second), 1);
            Assert.InRange(frame.Faces.Count, 2, 3); // tilted down: the top always shows, plus one or two sides
            Assert.All(frame.Faces, face => Assert.All(face.Points, point =>
            {
                Assert.InRange(point.X, 380, 620);
                Assert.InRange(point.Y, 380, 620);
            }));
            Assert.Equal(["Cube", "Title"], frame.Labels.Select(label => label.Text));
        }
    }

    [Fact]
    public void Project_PaintsTheFarSolidFirst_AndShadesWithoutBlowingOut()
    {
        // Two balls on the z axis: the one at z=-1 is behind and must be painted first.
        var figure = new InkFigure(Vector2.Zero, 200, [new FigurePart(FigureShape.Sphere, new(0, 0, 1), new(0, 0, 1), 0.3f, 0xFF0000, null), new FigurePart(FigureShape.Sphere, new(0, 0, -1), new(0, 0, -1), 0.3f, 0x00FF00, null)], null, TimeSpan.Zero);

        // Half a turn later the two have swapped places.
        foreach (var (at, first) in new[] { (TimeSpan.FromSeconds(0.7), 0x00FF00u), ((Figure3D.Turn / 2) + TimeSpan.FromSeconds(0.7), 0xFF0000u) })
        {
            var faces = Figure3D.Project(figure, at, 1).Faces;
            Assert.NotEmpty(faces);
            var hues = faces.Select(face => (face.Rgb & 0xFF0000) != 0 ? 0xFF0000u : 0x00FF00u).ToList();
            Assert.Equal(first, hues[0]);
            Assert.Equal(first == 0xFF0000u ? 0x00FF00u : 0xFF0000u, hues[^1]);
            Assert.All(faces, face => Assert.Equal(0u, face.Rgb & 0x0000FF)); // shading never adds another hue
        }
    }

    [Fact]
    public void Meshes_AreClosedEnoughToDraw()
    {
        Assert.Equal(6, Figure3D.Mesh(new FigurePart(FigureShape.Box, Vector3.Zero, Vector3.One, 0, 0, null)).Count());
        Assert.Equal(24 + 2, Figure3D.Mesh(new FigurePart(FigureShape.Cylinder, Vector3.Zero, Vector3.UnitY, 1, 0, null)).Count());
        Assert.Equal(24 + 1, Figure3D.Mesh(new FigurePart(FigureShape.Cone, Vector3.Zero, Vector3.UnitY, 1, 0, null)).Count());
        Assert.Empty(Figure3D.Mesh(new FigurePart(FigureShape.Cylinder, Vector3.One, Vector3.One, 1, 0, null)));
    }
}
