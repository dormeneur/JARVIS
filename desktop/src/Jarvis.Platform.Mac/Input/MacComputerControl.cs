using System.Text.Json;
using Jarvis.Contracts.Control;

namespace Jarvis.Platform.Mac.Input;

/// <summary>
/// "Let Jarvis drive" on a Mac: clicks, scrolls, Unicode typing and key combinations as posted
/// CGEvents (Accessibility permission), marked as ours so the shortcut tap ignores them. Before
/// typing, the keyboard goes back to the last app that wasn't Jarvis.
/// </summary>
public sealed class MacComputerControl : IComputerControl
{
    private const int ScrollLines = 3;
    private const int TakeBackPoints = 6;
    private int _targetPid;
    private (double X, double Y)? _placed;

    public IDisposable WatchForUser(Action tookBack)
    {
        Native.Mouse(out var x, out var y);
        _placed = (x, y);
        var fired = 0;
        void Fire()
        {
            if (Interlocked.Exchange(ref fired, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(_ => tookBack());
            }
        }

        var timer = new Timer(
            _ =>
            {
                Native.Mouse(out var nowX, out var nowY);
                if (_placed is { } placed && (Math.Abs(nowX - placed.X) > TakeBackPoints || Math.Abs(nowY - placed.Y) > TakeBackPoints))
                {
                    Fire();
                }
            },
            null,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50));
        Action onKey = Fire;
        MacShortcutHook.UserKeyDown += onKey;
        return new Watch(() =>
        {
            MacShortcutHook.UserKeyDown -= onKey;
            timer.Dispose();
            _placed = null;
        });
    }

    public bool Perform(ComputerAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RememberTarget();
        if (action.Kind is ComputerActionKind.Type or ComputerActionKind.Key && _targetPid != 0)
        {
            Native.ActivatePid(_targetPid);
            Thread.Sleep(80); // the app needs a moment to take the keyboard
        }

        switch (action.Kind)
        {
            case ComputerActionKind.Click:
                Place(action.X, action.Y);
                Native.PostMouse(1, action.X, action.Y);
                return true;
            case ComputerActionKind.DoubleClick:
                Place(action.X, action.Y);
                Native.PostMouse(2, action.X, action.Y);
                return true;
            case ComputerActionKind.RightClick:
                Place(action.X, action.Y);
                Native.PostMouse(3, action.X, action.Y);
                return true;
            case ComputerActionKind.ScrollUp or ComputerActionKind.ScrollDown:
                Place(action.X, action.Y);
                Native.PostMouse(0, action.X, action.Y);
                Native.PostScroll(action.Kind == ComputerActionKind.ScrollUp ? ScrollLines : -ScrollLines);
                return true;
            case ComputerActionKind.Type:
                Native.PostText(action.Text ?? string.Empty);
                return true;
            case ComputerActionKind.Key:
                if (MacKeys.ParseCombination(action.Text ?? string.Empty) is not { } combination)
                {
                    return false;
                }

                Native.PostKey(combination.KeyCode, combination.Flags);
                return true;
            case ComputerActionKind.Open:
                return OpenLink(action.Text ?? string.Empty);
            default:
                return false;
        }
    }

    /// <summary>Only web links: never a file path or another scheme a page could abuse.</summary>
    private static bool OpenLink(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" && Native.OpenUrl(parsed.AbsoluteUri) == 1;

    private void Place(double x, double y) => _placed = (x, y);

    private void RememberTarget()
    {
        using var front = JsonDocument.Parse(Native.Take(Native.FrontApp()) ?? "{}");
        if (front.RootElement.TryGetProperty("pid", out var pid) && pid.GetInt32() != Environment.ProcessId)
        {
            _targetPid = pid.GetInt32();
        }
    }

    private sealed class Watch(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
