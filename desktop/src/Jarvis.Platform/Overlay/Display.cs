using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Jarvis.Platform.Overlay;

/// <summary>A monitor in physical pixels on the virtual desktop, with its display scale.</summary>
internal sealed record Display(int Left, int Top, int Width, int Height, double Scale)
{
    public bool Contains(int x, int y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;

    /// <summary>Enumerates monitors. Requires a per-monitor-v2 DPI aware thread (the app manifest sets it).</summary>
    public static unsafe IReadOnlyList<Display> All()
    {
        var displays = new List<Display>();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT?)null, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (PInvoke.GetMonitorInfo(monitor, ref info))
            {
                var dpiX = 96u;
                _ = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out _);
                var bounds = info.rcMonitor;
                displays.Add(new Display(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top, dpiX / 96.0));
            }

            return true;
        }, 0);
        return displays;
    }
}
