using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// The little pill beside the cursor when it arrives at what it points at (DESIGN-FACTS.md §3):
/// 11 DIP text, 8×4 padding, radius 6, at most 300 wide, 10 right and 18 down from the cursor.
/// Painted once per text; moving it is a compositor offset.
/// </summary>
internal sealed class BubbleRenderer : IDisposable
{
    private const float FontDips = 11;
    private const float MaxTextDips = 300;
    private const float PaddingX = 8;
    private const float PaddingY = 4;
    private const float Radius = 6;
    private const float OffsetX = 10;
    private const float OffsetY = 18;
    private const float CanvasWidthDips = MaxTextDips + (2 * PaddingX) + 4;
    private const float CanvasHeightDips = 60;

    private readonly OverlayGraphics _graphics;
    private readonly IDCompositionVisual _visual;
    private IDCompositionSurface? _surface;
    private OverlayWindow? _host;
    private (string Text, uint Rgb, double Scale)? _drawn;

    public BubbleRenderer(OverlayGraphics graphics)
    {
        _graphics = graphics;
        _visual = graphics.Composition.CreateVisual();
    }

    /// <summary>Shows <paramref name="text"/> beside a cursor at (x, y) on <paramref name="host"/>, or hides it.</summary>
    public void Show(OverlayWindow? host, string? text, double x, double y, uint rgb)
    {
        if (host is null || string.IsNullOrEmpty(text))
        {
            Attach(null);
            return;
        }

        Attach(host);
        var scale = host.Display.Scale;
        if (_drawn != (text, rgb, scale))
        {
            Paint(text, rgb, scale);
        }

        _visual.SetOffsetX((float)(x - host.Display.Left + (OffsetX * scale)));
        _visual.SetOffsetY((float)(y - host.Display.Top + (OffsetY * scale)));
    }

    public void Dispose()
    {
        Attach(null);
        _surface?.Dispose();
        _visual.Dispose();
    }

    private void Attach(OverlayWindow? host)
    {
        if (ReferenceEquals(host, _host))
        {
            return;
        }

        _host?.Root.RemoveVisual(_visual);
        host?.Root.AddVisual(_visual, false, null);
        _host = host;
    }

    private void Paint(string text, uint rgb, double scale)
    {
        if (_surface is null || _drawn?.Scale != scale)
        {
            _surface?.Dispose();
            _graphics.Composition.CreateSurface(
                (uint)Math.Ceiling(CanvasWidthDips * scale), (uint)Math.Ceiling(CanvasHeightDips * scale),
                Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied, out _surface).CheckError();
            _visual.SetContent(_surface);
        }

        _drawn = (text, rgb, scale);
        var unit = (float)scale;
        var context = _surface!.BeginDraw<ID2D1DeviceContext>(null, out var offset);
        try
        {
            context.Clear(new Color4(0, 0, 0, 0)); // the whole surface, as a first paint must be
            using var format = _graphics.Text.CreateTextFormat("Segoe UI Variable Text", FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal, FontDips * unit);
            format.WordWrapping = WordWrapping.NoWrap;
            using var layout = _graphics.Text.CreateTextLayout(text, format, MaxTextDips * unit, 40 * unit);
            using var trimming = _graphics.Text.CreateEllipsisTrimmingSign(format);
            layout.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, trimming);

            var metrics = layout.Metrics;
            var width = Math.Min(metrics.Width, MaxTextDips * unit) + (2 * PaddingX * unit);
            var height = metrics.Height + (2 * PaddingY * unit);
            var origin = new Vector2(offset.X, offset.Y);
            using var fill = context.CreateSolidColorBrush(new Color4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1));
            using var white = context.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
            context.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(origin.X, origin.Y, width, height), Radius * unit, Radius * unit), fill);
            context.DrawTextLayout(origin + new Vector2(PaddingX * unit, PaddingY * unit), layout, white);
        }
        finally
        {
            _surface.EndDraw();
            context.Dispose();
        }
    }
}
