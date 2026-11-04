using System.Runtime.InteropServices;
using Jarvis.Contracts.Control;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Jarvis.Platform.Input;

/// <summary>
/// Real mouse and keyboard input through SendInput. Injected input is flagged by Windows, so
/// Jarvis's own shortcut hook never mistakes it for the user.
/// </summary>
public sealed class ComputerControl : IComputerControl
{
    private const int WheelNotches = 3;
    private static readonly uint OwnProcess = (uint)Environment.ProcessId;

    /// <summary>How far the mouse may drift from where Jarvis put it before it counts as the user's hand.</summary>
    private const int TakeBackPixels = 6;

    /// <summary>The last app window (not Jarvis's) that had focus: typing goes back there.</summary>
    private nint _target;

    /// <summary>Where Jarvis last put the mouse; a different position means the user moved it.</summary>
    private (int X, int Y)? _placed;

    public IDisposable WatchForUser(Action tookBack)
    {
        PInvoke.GetCursorPos(out var start);
        _placed = (start.X, start.Y);
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
                if (_placed is { } placed && PInvoke.GetCursorPos(out var now)
                    && (Math.Abs(now.X - placed.X) > TakeBackPixels || Math.Abs(now.Y - placed.Y) > TakeBackPixels))
                {
                    Fire();
                }
            },
            null,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(50));
        Action onKey = Fire;
        ShortcutHook.UserKeyDown += onKey;
        return new Watch(() =>
        {
            ShortcutHook.UserKeyDown -= onKey;
            timer.Dispose();
            _placed = null;
        });
    }

    public bool Perform(ComputerAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RememberTarget();
        if (action.Kind is ComputerActionKind.Type or ComputerActionKind.Key)
        {
            ReturnFocusToTarget();
        }

        try
        {
            return Do(action);
        }
        finally
        {
            RememberTarget();
        }
    }

    private bool Do(ComputerAction action)
    {
        switch (action.Kind)
        {
            case ComputerActionKind.Click:
                Click(action.X, action.Y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, times: 1);
                return true;
            case ComputerActionKind.DoubleClick:
                Click(action.X, action.Y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP, times: 2);
                return true;
            case ComputerActionKind.RightClick:
                Click(action.X, action.Y, MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP, times: 1);
                return true;
            case ComputerActionKind.ScrollUp or ComputerActionKind.ScrollDown:
                Place(action.X, action.Y);
                var delta = (action.Kind == ComputerActionKind.ScrollUp ? 1 : -1) * 120 * WheelNotches;
                Send([Mouse(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, delta)]);
                return true;
            case ComputerActionKind.Type:
                Type(action.Text ?? string.Empty);
                return true;
            case ComputerActionKind.Key:
                return PressCombination(action.Text ?? string.Empty);
            case ComputerActionKind.Open:
                return OpenLink(action.Text ?? string.Empty);
            default:
                return false;
        }
    }

    /// <summary>"ctrl+shift+s", "enter", "alt+f4"… Modifiers go down in order and up in reverse.</summary>
    internal static IReadOnlyList<ushort>? ParseCombination(string combination)
    {
        var keys = new List<ushort>();
        foreach (var part in combination.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ushort? key = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => 0x11,
                "SHIFT" => 0x10,
                "ALT" => 0x12,
                "WIN" or "WINDOWS" => 0x5B,
                "ENTER" or "RETURN" => 0x0D,
                "TAB" => 0x09,
                "ESC" or "ESCAPE" => 0x1B,
                "BACKSPACE" => 0x08,
                "DELETE" or "DEL" => 0x2E,
                "SPACE" => 0x20,
                "UP" => 0x26,
                "DOWN" => 0x28,
                "LEFT" => 0x25,
                "RIGHT" => 0x27,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" => 0x21,
                "PAGEDOWN" => 0x22,
                "PLAYPAUSE" or "PLAY" or "PAUSE" or "MEDIAPLAYPAUSE" => 0xB3,
                "NEXT" or "NEXTTRACK" => 0xB0,
                "PREVIOUS" or "PREVTRACK" => 0xB1,
                "VOLUMEUP" => 0xAF,
                "VOLUMEDOWN" => 0xAE,
                "MUTE" or "VOLUMEMUTE" => 0xAD,
                var name when name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name[1..], out var n) && n is >= 1 and <= 12 => (ushort)(0x6F + n),
                var name when name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]) => name[0],
                _ => null,
            };
            if (key is null)
            {
                return null;
            }

            keys.Add(key.Value);
        }

        return keys.Count > 0 ? keys : null;
    }

    private unsafe void RememberTarget()
    {
        var foreground = PInvoke.GetForegroundWindow();
        uint process;
        PInvoke.GetWindowThreadProcessId(foreground, &process);
        if (!foreground.IsNull && process != OwnProcess)
        {
            _target = foreground;
        }
    }

    /// <summary>If Jarvis's own window has focus (the approve click can give it), hand it back first.</summary>
    private unsafe void ReturnFocusToTarget()
    {
        uint process;
        PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), &process);
        if (process == OwnProcess && _target != 0)
        {
            Shell.WindowChrome.Activate(_target);
            Thread.Sleep(50); // let the focus change land before the keys do
        }
    }

    /// <summary>Without the browser extension: the default browser opens it. Web links only.</summary>
    private static bool OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return true;
    }

    private void Click(int x, int y, MOUSE_EVENT_FLAGS down, MOUSE_EVENT_FLAGS up, int times)
    {
        Place(x, y);
        for (var i = 0; i < times; i++)
        {
            Send([Mouse(down, 0), Mouse(up, 0)]);
        }
    }

    private static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var character in text.ReplaceLineEndings("\n"))
        {
            if (character == '\n')
            {
                inputs.Add(Key(0x0D, up: false));
                inputs.Add(Key(0x0D, up: true));
                continue;
            }

            inputs.Add(Unicode(character, up: false));
            inputs.Add(Unicode(character, up: true));
        }

        Send([.. inputs]);
    }

    private static bool PressCombination(string combination)
    {
        if (ParseCombination(combination) is not { } keys)
        {
            return false;
        }

        Send([.. keys.Select(key => Key(key, up: false)), .. keys.Reverse().Select(key => Key(key, up: true))]);
        return true;
    }

    /// <summary>Moves the mouse, telling the take-back watch first so it isn't mistaken for the user.</summary>
    private void Place(int x, int y)
    {
        if (_placed is not null)
        {
            _placed = (x, y);
        }

        PInvoke.SetCursorPos(x, y);
    }

    private sealed class Watch(Action stop) : IDisposable
    {
        private int _stopped;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                stop();
            }
        }
    }

    private static INPUT Mouse(MOUSE_EVENT_FLAGS flags, int data)
        => new() { type = INPUT_TYPE.INPUT_MOUSE, Anonymous = new() { mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) } } };

    private static INPUT Key(ushort key, bool up)
        => new() { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)key, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0 } } };

    private static INPUT Unicode(char character, bool up)
        => new()
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new() { ki = new KEYBDINPUT { wScan = character, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0) } },
        };

    private static void Send(INPUT[] inputs) => PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
}
