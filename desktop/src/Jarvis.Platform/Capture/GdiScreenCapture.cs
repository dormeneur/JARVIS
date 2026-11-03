using System.Runtime.InteropServices.WindowsRuntime;
using Jarvis.Contracts.Capture;
using Jarvis.Platform.Overlay;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Jarvis.Platform.Capture;

/// <summary>
/// Grabs each display from the composed desktop and encodes a downscaled JPEG for the model.
/// Windows marked as excluded from capture (Jarvis's own overlay and notch) are left out by the OS.
/// </summary>
public sealed class GdiScreenCapture : IScreenCapture
{
    /// <summary>Longest edge sent to the model: enough to read UI text, small enough to upload fast.</summary>
    private const int MaxDimension = 1280;
    private const float JpegQuality = 0.8f;

    public async Task<IReadOnlyList<CapturedScreen>> CaptureAsync(CancellationToken cancellationToken)
    {
        PInvoke.GetCursorPos(out var mouse);
        var displays = Display.All().OrderByDescending(display => display.Contains(mouse.X, mouse.Y)).ToList();
        var screens = new List<CapturedScreen>(displays.Count);
        foreach (var display in displays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixels = Grab(display);
            var scale = Math.Min(1.0, (double)MaxDimension / Math.Max(display.Width, display.Height));
            var width = (int)Math.Round(display.Width * scale);
            var height = (int)Math.Round(display.Height * scale);
            var jpeg = await EncodeAsync(pixels, display.Width, display.Height, width, height).ConfigureAwait(false);
            screens.Add(new CapturedScreen(
                new ScreenImage(jpeg, width, height, HasCursor: display.Contains(mouse.X, mouse.Y)),
                display.Left,
                display.Top,
                display.Width,
                display.Height,
                display.Scale));
        }

        return screens;
    }

    /// <summary>Copies one display into a top-down 32-bit BGRA buffer.</summary>
    private static unsafe byte[] Grab(Display display)
    {
        var screen = PInvoke.GetDC(HWND.Null);
        var memory = PInvoke.CreateCompatibleDC(screen);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = display.Width,
                biHeight = -display.Height, // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };

        void* bits;
        var bitmap = PInvoke.CreateDIBSection(memory, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var previous = PInvoke.SelectObject(memory, bitmap);
        try
        {
            PInvoke.BitBlt(memory, 0, 0, display.Width, display.Height, screen, display.Left, display.Top, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
            return new ReadOnlySpan<byte>(bits, display.Width * display.Height * 4).ToArray();
        }
        finally
        {
            PInvoke.SelectObject(memory, previous);
            PInvoke.DeleteObject(bitmap);
            PInvoke.DeleteDC(memory);
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
        }
    }

    private static async Task<ReadOnlyMemory<byte>> EncodeAsync(byte[] bgra, int width, int height, int scaledWidth, int scaledHeight)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new[] { new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(JpegQuality, PropertyType.Single)) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        encoder.BitmapTransform.ScaledWidth = (uint)scaledWidth;
        encoder.BitmapTransform.ScaledHeight = (uint)scaledHeight;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        await encoder.FlushAsync();

        var jpeg = new byte[stream.Size];
        await stream.GetInputStreamAt(0).ReadAsync(jpeg.AsBuffer(), (uint)jpeg.Length, InputStreamOptions.None);
        return jpeg;
    }
}
