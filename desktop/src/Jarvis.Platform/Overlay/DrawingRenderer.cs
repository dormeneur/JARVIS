using System.Numerics;
using Jarvis.Contracts.Overlay;
using Jarvis.Core.Drawing;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Microsoft.Extensions.Logging;
using Vortice.Mathematics;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// Paints hand-drawn marks over one display: each stroke is revealed as if the pen were moving,
/// glowing softly in the ink colour; labels pop in when their shape is done. Repaints only while
/// something is changing, so a finished drawing costs nothing per frame.
/// </summary>
internal sealed partial class DrawingRenderer : IDisposable
{
    private const float GlowDeviationDips = 3f;
    private const float GlowOpacity = 0.6f;
    private const float LabelFontDips = 14;
    private const float BadgeRadiusDips = 12;
    private static readonly TimeSpan LabelPop = TimeSpan.FromSeconds(0.18);
    private static readonly TimeSpan FadeOut = TimeSpan.FromSeconds(0.4);

    /// <summary>A drawing left alone this long after it finishes clears itself.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(30);

    private readonly OverlayGraphics _graphics;
    private readonly ILogger _logger;
    private readonly IDCompositionVisual _visual;
    private IDCompositionSurface? _surface;
    private Display? _surfaceDisplay;
    private OverlayWindow? _host;
    private ScreenDrawing? _drawing;
    private TimeSpan _shownAt;
    private TimeSpan? _fadeStartedAt;
    private bool _finishedPainting;
    private bool _surfaceInitialized;

    public DrawingRenderer(OverlayGraphics graphics, ILogger logger)
    {
        _graphics = graphics;
        _logger = logger;
        _visual = graphics.Composition.CreateVisual();
    }

    public void Show(ScreenDrawing drawing, TimeSpan now)
    {
        _drawing = drawing;
        _shownAt = now;
        _fadeStartedAt = null;
        _finishedPainting = false;
    }

    public void Clear(TimeSpan now)
    {
        if (_drawing is not null && _fadeStartedAt is null)
        {
            _fadeStartedAt = now;
        }
    }

    /// <summary>Stops showing the current drawing at once (after a failure, say).</summary>
    public void Abandon()
    {
        _drawing = null;
        AttachTo(null);
    }

    /// <summary>Displays were rebuilt; the old windows are gone.</summary>
    public void Detach()
    {
        _host = null;
        _surface?.Dispose();
        _surface = null;
        _surfaceDisplay = null;
        _finishedPainting = false;
    }

    /// <summary>Paints the current frame if anything changed.</summary>
    public void Render(IReadOnlyList<OverlayWindow> windows, TimeSpan now)
    {
        if (_drawing is not { } drawing)
        {
            return;
        }

        var elapsed = now - _shownAt;
        if (_fadeStartedAt is null && elapsed > drawing.Duration + Linger)
        {
            _fadeStartedAt = now;
        }

        var opacity = _fadeStartedAt is { } fadeStart ? 1 - (float)((now - fadeStart) / FadeOut) : 1f;
        if (opacity <= 0)
        {
            _drawing = null;
            AttachTo(null);
            return;
        }

        // ponytail: a figure turns, so it repaints every frame while shown (at most Linger); only its corner of the screen.
        var animating = elapsed <= drawing.Duration + LabelPop || _fadeStartedAt is not null || drawing.Figures.Count > 0;
        if (!animating && _finishedPainting)
        {
            return;
        }

        var host = HostFor(windows, drawing);
        if (host is null)
        {
            return;
        }

        AttachTo(host);
        EnsureSurface(host.Display);
        Paint(drawing, host.Display, elapsed, opacity);
        LogPainted(elapsed.TotalMilliseconds, drawing.Duration.TotalMilliseconds, opacity);
        _finishedPainting = !animating;
    }

    public void Dispose()
    {
        AttachTo(null);
        _surface?.Dispose();
        _visual.Dispose();
    }

    private void Paint(ScreenDrawing drawing, Display display, TimeSpan elapsed, float opacity)
    {
        // Repaint only around the marks. Redrawing (and blurring) the whole display every frame
        // floods the GPU and the screen falls seconds behind; the rest of the surface keeps its pixels.
        // A new surface's pixels are undefined, so DirectComposition rejects a partial first update.
        var dirty = _surfaceInitialized ? DirtyRect(drawing, display) : new Vortice.RawRect(0, 0, display.Width, display.Height);
        _surfaceInitialized = true;
        var context = _surface!.BeginDraw<ID2D1DeviceContext>(dirty, out var offset);
        try
        {
            context.Clear(new Color4(0, 0, 0, 0));
            var ink = Ink(drawing.Rgb, opacity);
            var toSurface = Matrix3x2.CreateTranslation(offset.X - dirty.Left - display.Left, offset.Y - dirty.Top - display.Top);

            using var strokes = RecordStrokes(drawing, elapsed, ink, toSurface);
            using var glow = new GaussianBlur(context) { StandardDeviation = GlowDeviationDips * drawing.Scale };
            glow.SetInput(0, strokes, true);
            using var glowFade = new Opacity(context) { Value = GlowOpacity };
            glowFade.SetInput(0, glow.Output, true);

            context.Transform = Matrix3x2.Identity;
            using (var glowImage = glowFade.Output)
            {
                context.DrawImage(glowImage);
            }

            context.DrawImage(strokes);
            var labels = drawing.Labels.ToList();
            foreach (var figure in drawing.Figures)
            {
                var frame = Figure3D.Project(figure, elapsed, drawing.Scale);
                PaintFaces(context, frame.Faces, opacity, toSurface);
                labels.AddRange(frame.Labels);
            }

            PaintLabels(context, labels, drawing.Scale, elapsed, ink, opacity, toSurface);
        }
        finally
        {
            _surface.EndDraw();
            context.Dispose();
        }
    }

    /// <summary>
    /// The part of the surface the drawing can touch, in surface pixels: every stroke point and label
    /// anchor, padded for stroke width, glow and label size, clipped to the display.
    /// </summary>
    private static Vortice.RawRect DirtyRect(ScreenDrawing drawing, Display display)
    {
        var points = drawing.Strokes.SelectMany(stroke => stroke.Points.Select(point => point.Position))
            .Concat(drawing.Labels.Select(label => label.Anchor))
            .Concat(drawing.Figures.SelectMany(figure => new[] { figure.Centre - new Vector2(figure.Size), figure.Centre + new Vector2(figure.Size) }))
            .ToList();
        var margin = 260 * drawing.Scale; // room for the widest label pill beside its anchor
        var left = points.Min(point => point.X) - margin - display.Left;
        var top = points.Min(point => point.Y) - margin - display.Top;
        var right = points.Max(point => point.X) + margin - display.Left;
        var bottom = points.Max(point => point.Y) + margin - display.Top;
        return new Vortice.RawRect(
            (int)Math.Clamp(left, 0, display.Width),
            (int)Math.Clamp(top, 0, display.Height),
            (int)Math.Clamp(right, 0, display.Width),
            (int)Math.Clamp(bottom, 0, display.Height));
    }

    /// <summary>Every stroke as far as the pen has got, recorded so the glow effect can blur it.</summary>
    private ID2D1CommandList RecordStrokes(ScreenDrawing drawing, TimeSpan elapsed, Color4 ink, Matrix3x2 toSurface)
    {
        var context = _graphics.Recorder;
        var commands = context.CreateCommandList();
        context.Target = commands;
        context.BeginDraw();
        context.Transform = toSurface;
        using var brush = context.CreateSolidColorBrush(ink);
        foreach (var stroke in drawing.Strokes)
        {
            var progress = (float)Math.Clamp((elapsed - stroke.Start) / stroke.Duration, 0, 1);
            if (progress <= 0)
            {
                continue;
            }

            var outline = StrokeOutline.Build(StrokeOutline.Prefix(stroke.Points, progress), stroke.Width, taper: stroke.Width * 2);
            if (outline.Count < 3)
            {
                continue;
            }

            using var geometry = _graphics.Factory.CreatePathGeometry();
            using (var sink = geometry.Open())
            {
                sink.BeginFigure(outline[0], FigureBegin.Filled);
                for (var i = 1; i < outline.Count; i++)
                {
                    sink.AddLine(outline[i]);
                }

                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }

            context.FillGeometry(geometry, brush);
        }

        context.EndDraw();
        context.Target = null;
        commands.Close();
        return commands;
    }

    /// <summary>A figure's shaded polygons, back to front; each is also outlined in its own colour so no seams show between neighbours.</summary>
    private void PaintFaces(ID2D1DeviceContext context, IReadOnlyList<Figure3D.Face> faces, float opacity, Matrix3x2 toSurface)
    {
        context.Transform = toSurface;
        using var brush = context.CreateSolidColorBrush(Ink(0, opacity));
        foreach (var face in faces)
        {
            using var geometry = _graphics.Factory.CreatePathGeometry();
            using (var sink = geometry.Open())
            {
                sink.BeginFigure(face.Points[0], FigureBegin.Filled);
                for (var i = 1; i < face.Points.Length; i++)
                {
                    sink.AddLine(face.Points[i]);
                }

                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }

            brush.Color = Ink(face.Rgb, opacity);
            context.FillGeometry(geometry, brush);
            context.DrawGeometry(geometry, brush, 1f);
        }

        context.Transform = Matrix3x2.Identity;
    }

    private void PaintLabels(ID2D1DeviceContext context, IReadOnlyList<InkLabel> labels, float scale, TimeSpan elapsed, Color4 ink, float opacity, Matrix3x2 toSurface)
    {
        using var fill = context.CreateSolidColorBrush(ink);
        using var white = context.CreateSolidColorBrush(new Color4(1, 1, 1, opacity));
        using var format = _graphics.Text.CreateTextFormat("Segoe UI Variable Display", FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, LabelFontDips * scale);
        var unit = scale;

        foreach (var label in labels)
        {
            var since = elapsed - label.Start;
            if (since < TimeSpan.Zero)
            {
                continue;
            }

            using var layout = _graphics.Text.CreateTextLayout(label.Text, format, 600 * unit, 100 * unit);
            var textSize = new Vector2(layout.Metrics.Width, layout.Metrics.Height);
            var pop = PopScale(since);

            Vector2 centre;
            if (label.Style == InkLabelStyle.Badge)
            {
                centre = label.Anchor;
                context.Transform = Matrix3x2.CreateScale(pop, centre) * toSurface;
                context.FillEllipse(new Ellipse(centre, BadgeRadiusDips * unit, BadgeRadiusDips * unit), fill);
            }
            else
            {
                var padding = new Vector2(9 * unit, 4 * unit);
                var size = textSize + (padding * 2);
                var left = label.GrowsLeft ? label.Anchor.X - size.X : label.Anchor.X;
                centre = new Vector2(left + (size.X / 2), label.Anchor.Y);
                context.Transform = Matrix3x2.CreateScale(pop, centre) * toSurface;
                var radius = size.Y / 2;
                context.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(left, centre.Y - radius, size.X, size.Y), radius, radius), fill);
            }

            context.DrawTextLayout(centre - (textSize / 2), layout, white);
        }

        context.Transform = Matrix3x2.Identity;
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Drawing painted at {ElapsedMs:F0} of {DurationMs:F0} ms, opacity {Opacity:F2}")]
    private partial void LogPainted(double elapsedMs, double durationMs, float opacity);

    /// <summary>Labels grow in with a slight overshoot (ease-out-back) over <see cref="LabelPop"/>.</summary>
    private static float PopScale(TimeSpan since)
    {
        var t = (float)Math.Clamp(since / LabelPop, 0, 1);
        const float Overshoot = 1.7f;
        var u = t - 1;
        return 0.6f + (0.4f * (1 + ((Overshoot + 1) * u * u * u) + (Overshoot * u * u)));
    }

    private static Color4 Ink(uint rgb, float opacity)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, opacity);

    /// <summary>The display the drawing is on: where its first mark is.</summary>
    private static OverlayWindow? HostFor(IReadOnlyList<OverlayWindow> windows, ScreenDrawing drawing)
    {
        var anchor = drawing.Strokes.Count > 0 ? drawing.Strokes[0].Points[0].Position
            : drawing.Labels.Count > 0 ? drawing.Labels[0].Anchor
            : drawing.Figures.Count > 0 ? drawing.Figures[0].Centre
            : (Vector2?)null;
        return anchor is { } point
            ? windows.FirstOrDefault(window => window.Display.Contains((int)point.X, (int)point.Y)) ?? windows.FirstOrDefault()
            : null;
    }

    private void AttachTo(OverlayWindow? host)
    {
        if (ReferenceEquals(host, _host))
        {
            return;
        }

        _host?.Root.RemoveVisual(_visual);
        // insertAbove + no reference = below every sibling, so the cursor always stays on top of ink.
        host?.Root.AddVisual(_visual, true, null);
        _host = host;
    }

    private void EnsureSurface(Display display)
    {
        if (_surface is not null && _surfaceDisplay == display)
        {
            return;
        }

        _surface?.Dispose();
        _graphics.Composition.CreateSurface((uint)display.Width, (uint)display.Height, Format.B8G8R8A8_UNorm, Vortice.DXGI.AlphaMode.Premultiplied, out _surface).CheckError();
        _visual.SetContent(_surface);
        _surfaceDisplay = display;
        _surfaceInitialized = false;
    }
}
