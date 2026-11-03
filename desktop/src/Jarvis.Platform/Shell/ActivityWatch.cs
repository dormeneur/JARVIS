using System.Diagnostics;
using Jarvis.Contracts.Activity;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Jarvis.Platform.Shell;

/// <summary>The foreground window and the user's idle time, from Win32.</summary>
public sealed class ActivityWatch : IActivityWatch
{
    private static readonly string[] PrivateWindows = ["InPrivate", "Incognito", "Private Browsing", "Private browsing"];

    public bool InFullscreen => Fullscreen.IsActive;

    public unsafe ForegroundWindow? Foreground()
    {
        var window = PInvoke.GetForegroundWindow();
        if (window.IsNull)
        {
            return null;
        }

        uint processId;
        PInvoke.GetWindowThreadProcessId(window, &processId);
        if (processId == Environment.ProcessId)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[256];
        int length;
        fixed (char* text = buffer)
        {
            length = PInvoke.GetWindowText(window, text, buffer.Length);
        }

        var title = new string(buffer[..length]);
        if (title.Length == 0 || PrivateWindows.Any(marker => title.Contains(marker, StringComparison.Ordinal)))
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new ForegroundWindow(process.ProcessName, title);
        }
        catch (ArgumentException)
        {
            return null; // it just exited
        }
    }

    public TimeSpan IdleFor()
    {
        var input = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        return PInvoke.GetLastInputInfo(ref input)
            ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - input.dwTime)
            : TimeSpan.Zero;
    }
}
