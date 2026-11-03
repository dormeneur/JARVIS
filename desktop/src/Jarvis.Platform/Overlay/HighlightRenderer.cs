using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// The highlighter trail drawn while the user circles something with the talk keys held: width 8
/// DIPs, alpha 0.55, fading over 1 s after release (DESIGN-FACTS.md §3). Repaints only while the
/// trail grows or fades; costs nothing otherwise.
/// </summary>
internal sealed class HighlightRenderer : IDisposable
{
    private const float WidthDips = 8;
    private const float Alpha = 0.55f;
    private static readonly TimeSpan FadeOut = TimeSpan.FromSeconds(1);

    private readonly OverlayGraphics _graphics;
    private readonly IDCompositionVisual _visual;
    private IDCompositionSurface? _surface;
    private OverlayWindow? _host;
    private (double X, double Y)[] _points = [];
    private uint _rgb;
    private TimeSpan? _fadeStartedAt;
    private int _paintedCount = -1;
    private float _paintedOpacity = -1;

    public HighlightRenderer(OverlayGraphics graphics)
    {
        _graphics = graphics;
        _visual = graphics.Composition.CreateVisual();
    }

    /// <summary>The live trail (desktop pixels) on <paramref name="host"/>'s monitor.</summary>
    public void Update(OverlayWindow host, IReadOnlyList<(double X, double Y)> points, uint rgb)
    {
        _fadeStartedAt = null;
        _rgb = rgb;
        if (!ReferenceEquals(host, _host))
        {
            Detach();
            _host = host;
            host.Root.AddVisual(_visual, false, null);
        }

        if (points.Count != _points.Length)
        {
            _points = [.. points];
        }
    }

    /// <summary>The keys came up: let the trail fade out.</summary>
    public void Release(TimeSpan now) => _fadeStartedAt ??= now;

    public void Render(TimeSpan now)
    {
        if (_host is null || _points.Length < 2)
        {
            return;
        }

        var opacity = _fadeStartedAt is { } fadeStart ? 1 - (float)((now - fadeStart) / FadeOut) : 1f;
        if (opacity <= 0)
        {
            Detach();
            return;
        }

        if (_paintedCount == _points.Length && Math.Abs(_paintedOpacity - opacity) < 0.02f)
        {
            return;
        }

        Paint(opacity);
        _paintedCount = _points.Length;
        _paintedOpacity = opacity;
    }

    public void Dispose()
    {
        Detach();
        _surface?.Dispose();
        _visual.Dispose();
    }

    private void Detach()
    {
        _host?.Root.RemoveVisual(_visual);
        _host = null;
        _points = [];
        _paintedCount = -1;
        _surface?.Dispose();
        _surface = null;
    }

    private void Paint(float opacity)
    {
        var display = _host!.Display;
        if (_surface is null)
        {
            _graphics.Composition.CreateSurface((uint)display.Width, (uint)display.Height, Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out _surface).CheckError();
            _visual.SetContent(_surface);
        }

        var context = _surface!.BeginDraw<ID2D1DeviceContext>(null, out var offset); // whole surface each time
        try
        {
            context.Clear(new Color4(0, 0, 0, 0));
            var color = new Color4(((_rgb >> 16) & 0xFF) / 255f, ((_rgb >> 8) & 0xFF) / 255f, (_rgb & 0xFF) / 255f, Alpha * opacity);
            using var brush = context.CreateSolidColorBrush(color);
            using var style = _graphics.Factory.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Round,
                EndCap = CapStyle.Round,
                LineJoin = LineJoin.Round,
            });
            using var path = _graphics.Factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                System.Numerics.Vector2 Local((double X, double Y) point) => new((float)(point.X - display.Left + offset.X), (float)(point.Y - display.Top + offset.Y));
                sink.BeginFigure(Local(_points[0]), FigureBegin.Hollow);
                for (var i = 1; i < _points.Length; i++)
                {
                    sink.AddLine(Local(_points[i]));
                }

                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }

            context.DrawGeometry(path, brush, WidthDips * (float)display.Scale, style);
        }
        finally
        {
            _surface.EndDraw();
            context.Dispose();
        }
    }
}
