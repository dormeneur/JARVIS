using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// The glow around every screen while Jarvis has the mouse and keyboard: a soft band along each
/// edge whose colours (Jarvis's accents) flow around the screen. Four thin strips per display, so
/// each frame repaints only the edges. Fades in and out; costs nothing when off.
/// </summary>
internal sealed class ControlBorderRenderer : IDisposable
{
    private const float ThicknessDips = 22;
    private const float EdgeAlpha = 1f;
    private const int Bands = 14;
    private const double FlowSeconds = 4; // one trip of the colours around the screen
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(300);

    // Blue, violet, pink, orange: the notch's accents.
    private static readonly Color4[] Palette =
    [
        new(0x33 / 255f, 0x80 / 255f, 1f, 1f),
        new(0x9B / 255f, 0x5C / 255f, 1f, 1f),
        new(1f, 0x5C / 255f, 0xA8 / 255f, 1f),
        new(1f, 0xA6 / 255f, 0x59 / 255f, 1f),
    ];

    private readonly OverlayGraphics _graphics;
    private readonly List<Strip> _strips = [];
    private bool _on;
    private TimeSpan _changedAt;
    private IReadOnlyList<OverlayWindow>? _builtFor;

    public ControlBorderRenderer(OverlayGraphics graphics)
    {
        _graphics = graphics;
    }

    public bool IsVisible => _strips.Count > 0;

    public void Set(bool on, TimeSpan now)
    {
        if (on != _on)
        {
            _on = on;
            _changedAt = now;
        }
    }

    public void Render(IReadOnlyList<OverlayWindow> windows, TimeSpan now)
    {
        var fade = (float)Math.Clamp((now - _changedAt) / Fade, 0, 1);
        var opacity = _on ? fade : 1 - fade;
        if (opacity <= 0)
        {
            Detach();
            return;
        }

        if (!ReferenceEquals(windows, _builtFor))
        {
            Detach();
            Build(windows);
        }

        var phase = (float)(now.TotalSeconds / FlowSeconds % 1);
        foreach (var strip in _strips)
        {
            Paint(strip, phase, opacity);
        }
    }

    public void Dispose() => Detach();

    private void Build(IReadOnlyList<OverlayWindow> windows)
    {
        foreach (var window in windows)
        {
            var display = window.Display;
            var thickness = (int)Math.Ceiling(ThicknessDips * display.Scale);
            var perimeter = 2f * (display.Width + display.Height);
            (int X, int Y, int Width, int Height, Edge Edge, float Start)[] edges =
            [
                (0, 0, display.Width, thickness, Edge.Top, 0),
                (display.Width - thickness, 0, thickness, display.Height, Edge.Right, display.Width / perimeter),
                (0, display.Height - thickness, display.Width, thickness, Edge.Bottom, (display.Width + display.Height) / perimeter),
                (0, 0, thickness, display.Height, Edge.Left, ((2f * display.Width) + display.Height) / perimeter),
            ];

            foreach (var (x, y, width, height, edge, start) in edges)
            {
                var visual = _graphics.Composition.CreateVisual();
                _graphics.Composition.CreateSurface((uint)width, (uint)height, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out var surface).CheckError();
                visual.SetContent(surface);
                visual.SetOffsetX(x);
                visual.SetOffsetY(y);
                window.Root.AddVisual(visual, false, null);
                window.RaiseToTop();
                var length = edge is Edge.Top or Edge.Bottom ? width : height;
                _strips.Add(new Strip(window, visual, surface, edge, width, height, thickness, start, length / perimeter));
            }
        }

        _builtFor = windows;
    }

    private void Paint(Strip strip, float phase, float opacity)
    {
        var context = strip.Surface.BeginDraw<ID2D1DeviceContext>(null, out var offset);
        try
        {
            context.Clear(new Color4(0, 0, 0, 0));
            var along = strip.Edge is Edge.Top or Edge.Bottom;
            var length = along ? strip.Width : strip.Height;

            // Colours along the edge: where this strip sits on the perimeter, shifted by the flow.
            var stops = new GradientStop[Palette.Length * 2 + 1];
            for (var i = 0; i < stops.Length; i++)
            {
                var position = (float)i / (stops.Length - 1);
                var aroundScreen = strip.Start + (position * strip.Share) - phase;
                stops[i] = new GradientStop(position, Colour(aroundScreen));
            }

            using var collection = context.CreateGradientStopCollection(stops);
            var origin = new Vector2(offset.X, offset.Y);
            var far = along ? origin + new Vector2(length, 0) : origin + new Vector2(0, length);

            // Clockwise: the bottom runs right to left and the left edge bottom to top.
            var reversed = strip.Edge is Edge.Bottom or Edge.Left;
            using var brush = context.CreateLinearGradientBrush(
                new LinearGradientBrushProperties { StartPoint = reversed ? far : origin, EndPoint = reversed ? origin : far },
                collection);

            // Bright at the screen edge, fading to nothing inward.
            var band = strip.Thickness / (float)Bands;
            for (var i = 0; i < Bands; i++)
            {
                var fromEdge = i * band;
                var falloff = 1 - ((float)i / Bands);
                brush.Opacity = EdgeAlpha * opacity * falloff * falloff;
                var rect = strip.Edge switch
                {
                    Edge.Top => new Rect(origin.X, origin.Y + fromEdge, strip.Width, band),
                    Edge.Bottom => new Rect(origin.X, origin.Y + strip.Height - fromEdge - band, strip.Width, band),
                    Edge.Left => new Rect(origin.X + fromEdge, origin.Y, band, strip.Height),
                    _ => new Rect(origin.X + strip.Width - fromEdge - band, origin.Y, band, strip.Height),
                };
                context.FillRectangle(rect, brush);
            }
        }
        finally
        {
            strip.Surface.EndDraw();
            context.Dispose();
        }
    }

    /// <summary>The palette as a loop: 0..1 around the screen, wrapping.</summary>
    private static Color4 Colour(float around)
    {
        var t = ((around % 1) + 1) % 1 * Palette.Length;
        var index = (int)t;
        var mix = t - index;
        var a = Palette[index % Palette.Length];
        var b = Palette[(index + 1) % Palette.Length];
        return new Color4(a.R + ((b.R - a.R) * mix), a.G + ((b.G - a.G) * mix), a.B + ((b.B - a.B) * mix), 1);
    }

    private void Detach()
    {
        foreach (var strip in _strips)
        {
            strip.Window.Root.RemoveVisual(strip.Visual);
            strip.Surface.Dispose();
            strip.Visual.Dispose();
        }

        _strips.Clear();
        _builtFor = null;
    }

    private enum Edge
    {
        Top,
        Right,
        Bottom,
        Left,
    }

    /// <param name="Start">Where the strip begins on the perimeter (0..1, clockwise from top-left).</param>
    /// <param name="Share">Its length as a share of the perimeter.</param>
    private sealed record Strip(OverlayWindow Window, IDCompositionVisual Visual, IDCompositionSurface Surface, Edge Edge, int Width, int Height, int Thickness, float Start, float Share);
}
