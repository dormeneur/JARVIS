using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace Jarvis.Platform.Shell;

/// <summary>
/// Whether the user is in something fullscreen (a game, a video, a presentation): the foreground
/// window covers its whole monitor, taskbar area included, or Windows reports a fullscreen
/// Direct3D app or presentation mode. (Windows' broader "busy" state also fires for ordinary
/// borderless windows, so it isn't used.) Cached for a second so per-frame callers stay cheap.
/// </summary>
public static class Fullscreen
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);
    private static readonly int OwnProcess = Environment.ProcessId;
    private static long _nextCheck;
    private static bool _active;

    public static bool IsActive
    {
        get
        {
            var now = Environment.TickCount64;
            if (now >= Interlocked.Read(ref _nextCheck))
            {
                _active = Check();
                Interlocked.Exchange(ref _nextCheck, now + (long)CacheFor.TotalMilliseconds);
            }

            return _active;
        }
    }

    private static unsafe bool Check()
    {
        if (PInvoke.SHQueryUserNotificationState(out var state).Succeeded
            && state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        var window = PInvoke.GetForegroundWindow();
        if (window.IsNull || IsDesktopOrOwn(window) || !PInvoke.GetWindowRect(window, out var bounds))
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        var monitor = PInvoke.MonitorFromWindow(window, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetMonitorInfo(monitor, ref info)
               && bounds.left <= info.rcMonitor.left && bounds.top <= info.rcMonitor.top
               && bounds.right >= info.rcMonitor.right && bounds.bottom >= info.rcMonitor.bottom;
    }

    private static unsafe bool IsDesktopOrOwn(HWND window)
    {
        uint process;
        PInvoke.GetWindowThreadProcessId(window, &process);
        if (process == OwnProcess)
        {
            return true;
        }

        Span<char> name = stackalloc char[32];
        fixed (char* buffer = name)
        {
            var length = PInvoke.GetClassName(window, buffer, name.Length);
            var className = new string(buffer, 0, length);
            return className is "Progman" or "WorkerW" or "Shell_TrayWnd";
        }
    }
}
