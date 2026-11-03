using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// Blocks until the next frame. Waits on the desktop compositor (vsync-aligned), but DwmFlush
/// returns at once when nothing on screen is changing, so short waits are topped up with a
/// high-resolution timer to keep an idle overlay from spinning a core.
/// </summary>
internal sealed class FramePacer : IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 60);

    private readonly SafeFileHandle _timer;
    private long _lastFrame = Stopwatch.GetTimestamp();

    public FramePacer()
    {
        _timer = PInvoke.CreateWaitableTimerEx(
            null,
            null,
            PInvoke.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            (uint)SYNCHRONIZATION_ACCESS_RIGHTS.TIMER_ALL_ACCESS);
    }

    public unsafe void WaitForNextFrame()
    {
        PInvoke.DwmFlush();

        var remaining = FrameInterval - Stopwatch.GetElapsedTime(_lastFrame);
        if (remaining > TimeSpan.FromMilliseconds(1))
        {
            if (!_timer.IsInvalid)
            {
                // Negative due time = relative, in 100 ns units.
                var dueTime = -remaining.Ticks;
                if (PInvoke.SetWaitableTimerEx(_timer, dueTime, 0, null, null, null, 0))
                {
                    PInvoke.WaitForSingleObject(_timer, 100);
                }
            }
            else
            {
                Thread.Sleep(remaining);
            }
        }

        _lastFrame = Stopwatch.GetTimestamp();
    }

    public void Dispose() => _timer.Dispose();
}
