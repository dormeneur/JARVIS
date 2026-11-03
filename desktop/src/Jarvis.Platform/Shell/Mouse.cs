using Windows.Win32;

namespace Jarvis.Platform.Shell;

/// <summary>The real mouse right now: where it is (physical pixels) and whether its left button is down.</summary>
public static class Mouse
{
    public static (int X, int Y) Position => PInvoke.GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    public static bool LeftDown => (PInvoke.GetAsyncKeyState(0x01) & 0x8000) != 0;
}
