using System.Runtime.InteropServices;
using Jarvis.Contracts.Capture;

namespace Jarvis.Platform.Mac.Capture;

/// <summary>
/// ScreenCaptureKit screenshots of every display, the mouse's first, with Jarvis's own windows
/// (cursor, ink, notch) filtered out by the OS. Longest edge 1280 px, JPEG quality 0.8, as on
/// Windows. Desktop coordinates are points on the y-down desktop (see Screens).
/// </summary>
public sealed unsafe class MacScreenCapture : IScreenCapture
{
    private const int MaxDimension = 1280;
    private const double JpegQuality = 0.8;

    /// <summary>Screen Recording is allowed (System Settings → Privacy &amp; Security).</summary>
    public static bool Allowed => Native.ScreenAccess(0) == 1;

    public const string SettingsPage = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture";

    public Task<IReadOnlyList<CapturedScreen>> CaptureAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<CapturedScreen>>(
            () =>
            {
                var screens = new List<CapturedScreen>();
                var handle = GCHandle.Alloc(screens);
                try
                {
                    var count = Native.Capture(MaxDimension, JpegQuality, &OnCaptured, GCHandle.ToIntPtr(handle));
                    if (count == -1)
                    {
                        Native.ScreenAccess(1); // shows the system prompt the first time
                        throw new UnauthorizedAccessException("Jarvis needs Screen Recording permission (System Settings → Privacy & Security → Screen Recording).");
                    }
                }
                finally
                {
                    handle.Free();
                }

                Native.Mouse(out var x, out var y);
                return screens.Select(screen => screen with
                {
                    Image = screen.Image with { HasCursor = x >= screen.Left && x < screen.Left + screen.Width && y >= screen.Top && y < screen.Top + screen.Height },
                }).ToList();
            },
            cancellationToken);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnCaptured(nint context, byte* jpeg, int length, int width, int height, double left, double top, double displayWidth, double displayHeight, double scale)
    {
        if (GCHandle.FromIntPtr(context).Target is List<CapturedScreen> screens)
        {
            var image = new ScreenImage(new ReadOnlySpan<byte>(jpeg, length).ToArray(), width, height, HasCursor: false);
            screens.Add(new CapturedScreen(image, (int)left, (int)top, (int)displayWidth, (int)displayHeight, Scale: 1.0));
        }
    }
}
