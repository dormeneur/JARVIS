using System.Numerics;
using Jarvis.Core.Cursor;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// Draws the companion cursor into one small DirectComposition surface and moves it by changing
/// the visual's offset, so a mouse move costs a compositor property change, not a repaint.
/// </summary>
internal sealed class CursorRenderer : IDisposable
{
    /// <summary>Square drawing area around the cursor, in DIPs; fits the glow and the mid-flight swell.</summary>
    private const float CanvasDips = 72;
    private const float TriangleSide = 16;

    /// <summary>The dwell ring sits down and to the right of the cursor, clear of the triangle (ours).</summary>
    private const float DwellRingOffset = 16;
    private const float CornerRadiusFraction = 0.09f;
    private const float GlowDeviationDips = 3.5f;
    private const float GlowOpacity = 0.75f;

    private readonly OverlayGraphics _graphics;
    private readonly IDCompositionVisual _visual;
    private IDCompositionSurface? _surface;
    private Appearance? _drawn;
    private double _surfaceScale;
    private OverlayWindow? _host;

    public CursorRenderer(OverlayGraphics graphics)
    {
        _graphics = graphics;
        _visual = graphics.Composition.CreateVisual();
    }

    /// <summary>Puts the cursor on <paramref name="host"/>'s monitor, or hides it when <see langword="null"/>.</summary>
    public void AttachTo(OverlayWindow? host)
    {
        if (ReferenceEquals(host, _host))
        {
            return;
        }

        _host?.Root.RemoveVisual(_visual);
        host?.Root.AddVisual(_visual, false, null);
        _host = host;
    }

    public void Render(CursorPose pose, uint rgb)
    {
        if (_host is null)
        {
            return;
        }

        var scale = _host.Display.Scale;
        EnsureSurface(scale);

        var half = CanvasDips * (float)scale / 2;
        _visual.SetOffsetX((float)(pose.X - _host.Display.Left) - half);
        _visual.SetOffsetY((float)(pose.Y - _host.Display.Top) - half);

        // Moving is a compositor property change; only repaint when the cursor looks different.
        var appearance = new Appearance(Math.Round(pose.RotationDegrees, 1), Math.Round(pose.Scale, 3), pose.ThinkingSeconds, pose.ListeningLevel, rgb, scale, pose.Dwell);
        if (appearance == _drawn)
        {
            return;
        }

        _drawn = appearance;
        var context = _surface!.BeginDraw<ID2D1DeviceContext>(null, out var offset);
        try
        {
            context.Clear(new Color4(0, 0, 0, 0));
            var color = new Color4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1);
            var centre = Matrix3x2.CreateScale((float)(scale * pose.Scale))
                * Matrix3x2.CreateTranslation(offset.X + half, offset.Y + half);

            using var shape = RecordShape(pose, color, centre);
            using var glow = new GaussianBlur(context) { StandardDeviation = GlowDeviationDips * (float)scale };
            glow.SetInput(0, shape, true);

            using var glowOpacity = new Opacity(context) { Value = GlowOpacity };
            glowOpacity.SetInput(0, glow.Output, true);

            context.Transform = Matrix3x2.Identity;
            using (var glowImage = glowOpacity.Output)
            {
                context.DrawImage(glowImage);
            }

            context.DrawImage(shape);
        }
        finally
        {
            _surface.EndDraw();
            context.Dispose();
        }
    }

    public void Dispose()
    {
        AttachTo(null);
        _surface?.Dispose();
        _visual.Dispose();
    }

    /// <summary>
    /// Records the triangle (or the thinking dots) into a command list usable as an effect input.
    /// Uses its own context: the surface's context is already mid-draw and must keep its target.
    /// </summary>
    private ID2D1CommandList RecordShape(CursorPose pose, Color4 color, Matrix3x2 centre)
    {
        var context = _graphics.Recorder;
        var commands = context.CreateCommandList();
        context.Target = commands;
        context.BeginDraw();
        using var brush = context.CreateSolidColorBrush(color);

        if (pose.ListeningLevel is { } level)
        {
            // Upright bars, centred, growing from the middle.
            context.Transform = centre;
            const float pitch = (float)(ListeningBars.BarWidth + ListeningBars.Gap);
            var radius = (float)ListeningBars.CornerRadius;
            for (var bar = 0; bar < ListeningBars.Count; bar++)
            {
                var height = (float)ListeningBars.Height(bar, level);
                var left = ((bar - ((ListeningBars.Count - 1) / 2f)) * pitch) - ((float)ListeningBars.BarWidth / 2);
                var rect = new Vortice.RawRectF(left, -height / 2, left + (float)ListeningBars.BarWidth, height / 2);
                context.FillRoundedRectangle(new RoundedRectangle(rect, radius, radius), brush);
            }
        }
        else if (pose.ThinkingSeconds is { } seconds)
        {
            for (var dot = 0; dot < 3; dot++)
            {
                var p = ThinkingDots.Pose(dot, seconds);
                context.Transform = Matrix3x2.CreateRotation((float)p.AngleRadians)
                    * Matrix3x2.CreateTranslation((float)p.X, (float)p.Y)
                    * centre;
                context.FillEllipse(new Ellipse(Vector2.Zero, (float)p.RadiusAlong, (float)(p.RadiusAlong * p.AcrossScale)), brush);
            }
        }
        else
        {
            context.Transform = Matrix3x2.CreateRotation((float)(pose.RotationDegrees * Math.PI / 180)) * centre;
            using var triangle = BuildRoundedTriangle();
            context.FillGeometry(triangle, brush);
        }

        if (pose.Dwell is { } dwell)
        {
            DrawDwellRing(context, brush, color, centre, (float)dwell);
        }

        context.EndDraw();
        context.Target = null;
        commands.Close();
        return commands;
    }

    /// <summary>
    /// The ring beside the cursor while it rests on the notch: 16 DIPs across with a 2.5 DIP line
    /// (DESIGN-FACTS.md §3), a faint full track under an arc that fills clockwise from the top.
    /// </summary>
    private void DrawDwellRing(ID2D1DeviceContext context, ID2D1SolidColorBrush brush, Color4 color, Matrix3x2 centre, float fill)
    {
        const float line = 2.5f;
        const float radius = (16 - line) / 2;
        var at = new Vector2(DwellRingOffset, DwellRingOffset);
        context.Transform = centre;
        using var track = context.CreateSolidColorBrush(new Color4(color.R, color.G, color.B, 0.25f));
        context.DrawEllipse(new Ellipse(at, radius, radius), track, line);
        if (fill <= 0)
        {
            return;
        }

        var start = -MathF.PI / 2;
        var end = start + (2 * MathF.PI * Math.Min(fill, 0.999f));
        using var arc = _graphics.Factory.CreatePathGeometry();
        using (var sink = arc.Open())
        {
            sink.BeginFigure(at + (new Vector2(MathF.Cos(start), MathF.Sin(start)) * radius), FigureBegin.Hollow);
            sink.AddArc(new ArcSegment
            {
                Point = at + (new Vector2(MathF.Cos(end), MathF.Sin(end)) * radius),
                Size = new Vortice.Mathematics.Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                ArcSize = fill > 0.5f ? ArcSize.Large : ArcSize.Small,
            });
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }

        using var round = _graphics.Factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round });
        context.DrawGeometry(arc, brush, line, round);
    }

    /// <summary>Equilateral triangle centred on the origin, tip up, corners rounded with quadratic curves.</summary>
    private ID2D1PathGeometry BuildRoundedTriangle()
    {
        var circumradius = TriangleSide / MathF.Sqrt(3);
        var vertices = new Vector2[3];
        for (var i = 0; i < 3; i++)
        {
            var angle = (-MathF.PI / 2) + (i * 2 * MathF.PI / 3);
            vertices[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * circumradius;
        }

        // Distance from a 60° corner at which a fillet of radius r touches each edge: r·cot(30°).
        var inset = TriangleSide * CornerRadiusFraction * MathF.Sqrt(3);
        var geometry = _graphics.Factory.CreatePathGeometry();
        using var sink = geometry.Open();
        for (var i = 0; i < 3; i++)
        {
            var corner = vertices[i];
            var entry = corner + (Vector2.Normalize(vertices[(i + 2) % 3] - corner) * inset);
            var exit = corner + (Vector2.Normalize(vertices[(i + 1) % 3] - corner) * inset);
            if (i == 0)
            {
                sink.BeginFigure(entry, FigureBegin.Filled);
            }
            else
            {
                sink.AddLine(entry);
            }

            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = corner, Point2 = exit });
        }

        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geometry;
    }

    private void EnsureSurface(double scale)
    {
        if (_surface is not null && Math.Abs(scale - _surfaceScale) < 0.001)
        {
            return;
        }

        _surface?.Dispose();
        var pixels = (uint)Math.Ceiling(CanvasDips * scale);
        _graphics.Composition.CreateSurface(pixels, pixels, Format.B8G8R8A8_UNorm, Vortice.DXGI.AlphaMode.Premultiplied, out _surface).CheckError();
        _visual.SetContent(_surface);
        _surfaceScale = scale;
        _drawn = null;
    }

    /// <summary>Everything that changes the cursor's pixels (position is not among them).</summary>
    private readonly record struct Appearance(double Rotation, double Scale, double? ThinkingSeconds, double? ListeningLevel, uint Rgb, double DisplayScale, double? Dwell);
}
