using System.Numerics;

namespace Jarvis.Contracts.Overlay;

/// <summary>
/// Hand-drawn marks over the user's screen: circles, boxes, arrows, numbered steps, labels.
/// Thread-safe. Coordinates are physical pixels on the virtual desktop.
/// </summary>
public interface IScreenDrawing
{
    /// <summary>Replaces whatever is drawn with <paramref name="drawing"/>, revealed as if being drawn.</summary>
    void Show(ScreenDrawing drawing);

    /// <summary>Fades the drawing away.</summary>
    void Clear();
}

/// <param name="Rgb">Ink colour, 0xRRGGBB (the user's cursor colour).</param>
/// <param name="Scale">Display scale of the screen being drawn on (1.0 = 96 DPI); sizes labels.</param>
/// <param name="Strokes">Pen strokes in drawing order.</param>
/// <param name="Labels">Words and numbers, each appearing at its own time.</param>
/// <param name="Figures">Turning 3D figures, if any.</param>
public sealed record ScreenDrawing(uint Rgb, float Scale, IReadOnlyList<InkStroke> Strokes, IReadOnlyList<InkLabel> Labels, IReadOnlyList<InkFigure>? Figures = null)
{
    /// <summary>How long a figure takes to grow in.</summary>
    public static readonly TimeSpan FigureGrow = TimeSpan.FromSeconds(0.6);

    public IReadOnlyList<InkFigure> Figures { get; init; } = Figures ?? [];

    /// <summary>When the last mark has finished appearing.</summary>
    public TimeSpan Duration => Strokes.Select(stroke => stroke.Start + stroke.Duration)
        .Concat(Labels.Select(label => label.Start))
        .Concat(Figures.Select(figure => figure.Start + FigureGrow))
        .DefaultIfEmpty(TimeSpan.Zero)
        .Max();
}

/// <param name="Points">The pen's path.</param>
/// <param name="Width">Stroke width at full pressure, physical pixels.</param>
/// <param name="Start">When the pen starts this stroke, from the moment the drawing is shown.</param>
/// <param name="Duration">How long the pen takes to draw it.</param>
public sealed record InkStroke(IReadOnlyList<InkPoint> Points, float Width, TimeSpan Start, TimeSpan Duration);

/// <param name="Anchor">Centre of a badge; for a pill, the middle of its left edge (or right edge, see <paramref name="GrowsLeft"/>).</param>
/// <param name="GrowsLeft">The pill extends leftwards from the anchor, e.g. so it doesn't cover an arrow heading right.</param>
public sealed record InkLabel(string Text, Vector2 Anchor, InkLabelStyle Style, TimeSpan Start, bool GrowsLeft = false);

public enum InkLabelStyle
{
    /// <summary>White words in a rounded pill of the ink colour.</summary>
    Pill,

    /// <summary>A number in a filled circle of the ink colour.</summary>
    Badge,
}

/// <summary>
/// A small 3D model that floats over the screen and slowly turns, to show a shape or how parts fit.
/// </summary>
/// <param name="Centre">Where its middle sits, physical pixels on the virtual desktop.</param>
/// <param name="Size">Roughly how wide it looks, physical pixels.</param>
/// <param name="Parts">Solids in the figure's own space: -1..1 on each axis, y up, z toward the viewer.</param>
/// <param name="Title">A label under the figure.</param>
/// <param name="Start">When it starts growing in, from the moment the drawing is shown.</param>
public sealed record InkFigure(Vector2 Centre, float Size, IReadOnlyList<FigurePart> Parts, string? Title, TimeSpan Start);

/// <summary>One solid of a figure, in figure space.</summary>
/// <param name="From">Box: one corner. Sphere: the centre. Cylinder and cone: the middle of the base.</param>
/// <param name="To">Box: the opposite corner. Sphere: the centre. Cylinder: the middle of the top. Cone: the tip.</param>
/// <param name="Radius">Sphere, cylinder and cone radius; unused for a box.</param>
/// <param name="Rgb">Colour, 0xRRGGBB, before shading.</param>
public sealed record FigurePart(FigureShape Shape, Vector3 From, Vector3 To, float Radius, uint Rgb, string? Label);

public enum FigureShape
{
    Box,
    Sphere,
    Cylinder,
    Cone,
}

/// <param name="Pressure">0–1; drives how thick the ink is at this point.</param>
public readonly record struct InkPoint(Vector2 Position, float Pressure);
