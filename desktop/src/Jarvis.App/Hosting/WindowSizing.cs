using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Jarvis.App.Hosting;

/// <summary>
/// AppWindow works in physical pixels; layouts are designed in DIPs. These helpers convert so a
/// window is the same visual size at 100 %, 125 % or 200 % display scaling.
/// </summary>
internal static partial class WindowSizing
{
    private const double DefaultDpi = 96;

    public static void ResizeDips(this Window window, double width, double height)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / DefaultDpi;
        window.AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
