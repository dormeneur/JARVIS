using System.Numerics;
using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Overlay;

namespace Jarvis.Core.Drawing;

/// <summary>
/// Turns a <see cref="DrawingScript"/> into ink on a real display: maps screenshot pixels to the
/// desktop, sketches each mark, and schedules the strokes one after another so the drawing appears
/// as if someone were drawing it.
/// </summary>
public static class SceneBuilder
{
    private const float StrokeWidthDips = 3.2f;
    private const float PenSpeedDipsPerSecond = 1100;
    private const float PaddingDips = 6;
    private static readonly TimeSpan MinimumStroke = TimeSpan.FromSeconds(0.12);
    private static readonly TimeSpan MaximumStroke = TimeSpan.FromSeconds(0.45);
    private static readonly TimeSpan PenLift = TimeSpan.FromSeconds(0.06);
    private const int MaximumFigures = 3;
    private const int MaximumParts = 16;

    /// <param name="screen">The capture the brain was looking at when it wrote the script.</param>
    /// <param name="rgb">Ink colour, 0xRRGGBB.</param>
    /// <param name="displayScale">The display's scale (1.0 = 96 DPI).</param>
    /// <param name="seed">Seeds the hand-drawn wobble; fixed in tests, random in the app.</param>
    public static ScreenDrawing Build(DrawingScript script, CapturedScreen screen, uint rgb, float displayScale, int seed)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(screen);
        var unit = displayScale;
        var sketch = new Sketch(new Random(seed), unit);
        var strokes = new List<InkStroke>();
        var labels = new List<InkLabel>();
        var figures = new List<InkFigure>();
        var clock = TimeSpan.Zero;

        Vector2 Map(float x, float y)
        {
            var (desktopX, desktopY) = screen.ToDesktop(x, y);
            return new Vector2(desktopX, desktopY);
        }

        void Draw(IReadOnlyList<InkPoint> path)
        {
            var length = 0f;
            for (var i = 1; i < path.Count; i++)
            {
                length += Vector2.Distance(path[i - 1].Position, path[i].Position);
            }

            var duration = TimeSpan.FromSeconds(Math.Clamp(length / (PenSpeedDipsPerSecond * unit), MinimumStroke.TotalSeconds, MaximumStroke.TotalSeconds));
            strokes.Add(new InkStroke(path, StrokeWidthDips * unit, clock, duration));
            clock += duration + PenLift;
        }

        // Badges are known up front, so a label written before its badge can still dodge it.
        var badges = script.Commands.OfType<DrawCommand.Step>().Select(step => Map(step.X, step.Y)).ToList();

        void Label(string? text, Vector2 anchor, InkLabelStyle style = InkLabelStyle.Pill, bool growsLeft = false)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (style == InkLabelStyle.Pill && !growsLeft)
                {
                    anchor = BesideBadges(text.Trim(), anchor, badges, unit);
                }

                labels.Add(new InkLabel(text.Trim(), anchor, style, clock, growsLeft));
                clock += PenLift;
            }
        }

        // A label sits above its shape, or below it when the shape hugs the top of the screen.
        Vector2 LabelAbove(Vector2 topLeft, Vector2 bottomLeft)
            => topLeft.Y - (18 * unit) > screen.Top + (8 * unit) ? topLeft - new Vector2(0, 18 * unit) : bottomLeft + new Vector2(0, 18 * unit);

        var padding = new Vector2(PaddingDips * unit);
        foreach (var command in script.Commands)
        {
            switch (command)
            {
                case DrawCommand.Box box:
                {
                    var topLeft = Map(box.X, box.Y) - padding;
                    var bottomRight = Map(box.X + box.Width, box.Y + box.Height) + padding;
                    var topRight = new Vector2(bottomRight.X, topLeft.Y);
                    var bottomLeft = new Vector2(topLeft.X, bottomRight.Y);
                    Draw(sketch.Line(topLeft, topRight));
                    Draw(sketch.Line(topRight, bottomRight));
                    Draw(sketch.Line(bottomRight, bottomLeft));
                    Draw(sketch.Line(bottomLeft, topLeft));
                    Label(box.Label, LabelAbove(topLeft, bottomLeft));
                    break;
                }

                case DrawCommand.Ring ring:
                {
                    var topLeft = Map(ring.X, ring.Y);
                    var bottomRight = Map(ring.X + ring.Width, ring.Y + ring.Height);
                    var centre = (topLeft + bottomRight) / 2;
                    // An ellipse through the corners of the box would be √2 bigger; this sits just outside it.
                    var radius = ((bottomRight - topLeft) / 2 * 1.15f) + new Vector2(8 * unit);
                    Draw(sketch.Ring(centre, radius));
                    Label(ring.Label, LabelAbove(centre - radius, centre + new Vector2(-radius.X, radius.Y)));
                    break;
                }

                case DrawCommand.Arrow arrow:
                {
                    var tail = Map(arrow.FromX, arrow.FromY);
                    var target = Map(arrow.ToX, arrow.ToY);
                    var direction = target - tail;
                    if (direction == Vector2.Zero)
                    {
                        break;
                    }

                    // Stop short of the target so the head doesn't cover what it points at.
                    var tip = target - (Vector2.Normalize(direction) * 6 * unit);
                    Draw(sketch.Line(tail, tip));
                    var (left, right) = sketch.ArrowHead(tip, direction);
                    Draw(left);
                    Draw(right);
                    // The label sits behind the tail, growing away from the shaft.
                    var headsRight = direction.X >= 0;
                    Label(arrow.Label, tail + new Vector2(headsRight ? -8 * unit : 8 * unit, 0), growsLeft: headsRight);
                    break;
                }

                case DrawCommand.Step step:
                    Label(step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), Map(step.X, step.Y), InkLabelStyle.Badge);
                    break;

                case DrawCommand.Note note:
                    Label(note.Text, Map(note.X, note.Y));
                    break;

                case DrawCommand.Figure figure when figures.Count < MaximumFigures:
                    var middle = Map(figure.X, figure.Y);
                    figures.Add(new InkFigure(middle, Map(figure.X + figure.Size, figure.Y).X - middle.X, [], figure.Title, clock));
                    clock += ScreenDrawing.FigureGrow;
                    break;

                case DrawCommand.Solid solid when figures.Count > 0 && figures[^1].Parts.Count < MaximumParts:
                    figures[^1] = figures[^1] with { Parts = [.. figures[^1].Parts, new FigurePart(solid.Shape, solid.From, solid.To, solid.Radius, solid.Rgb ?? rgb, solid.Label)] };
                    break;
            }
        }

        return new ScreenDrawing(rgb, displayScale, strokes, labels, figures.Where(figure => figure.Parts.Count > 0).ToList());
    }

    /// <summary>
    /// If a numbered badge would sit on this pill, lifts the pill above the badge. Lifting (rather
    /// than sliding sideways) can't push it into a neighbouring shape's label. The pill's width is
    /// estimated from its text; the renderer knows it exactly, but this is plenty.
    /// </summary>
    private static Vector2 BesideBadges(string text, Vector2 anchor, List<Vector2> badges, float unit)
    {
        const float BadgeRadius = 12;
        var estimatedWidth = ((text.Length * 7.5f) + 18) * unit;
        foreach (var badge in badges)
        {
            var overlapsHorizontally = badge.X + (BadgeRadius * unit) > anchor.X && badge.X - (BadgeRadius * unit) < anchor.X + estimatedWidth;
            var overlapsVertically = Math.Abs(badge.Y - anchor.Y) < 24 * unit;
            if (overlapsHorizontally && overlapsVertically)
            {
                anchor = anchor with { Y = badge.Y - ((BadgeRadius + 16) * unit) };
            }
        }

        return anchor;
    }
}
