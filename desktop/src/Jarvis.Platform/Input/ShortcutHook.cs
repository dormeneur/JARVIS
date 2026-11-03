using System.Runtime.InteropServices;
using System.Threading.Channels;
using Jarvis.Core;
using Jarvis.Core.Companion;
using Jarvis.Core.Dictation;
using Jarvis.Core.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Jarvis.Platform.Input;

/// <summary>
/// Watches the keyboard system-wide for the shortcuts (hold Ctrl+Alt to talk, Ctrl+Win to dictate,
/// double-tap Ctrl to type). The hold chords follow the "Shortcuts" settings live.
/// A low-level hook is the only way to see modifier-only chords; RegisterHotKey needs a normal key.
/// Keys are observed, never swallowed, so every app still gets Ctrl, Alt and Win as usual.
/// </summary>
public sealed partial class ShortcutHook : IHostedService, IDisposable
{
    /// <summary>An unassigned key code. Pressing it while Win is down stops Win's release from opening Start.</summary>
    private const VIRTUAL_KEY StartMenuMask = (VIRTUAL_KEY)0xFF;

    private readonly CompanionSession _companion;
    private readonly DictationSession _dictation;
    private readonly PauseSwitch _pause;
    private readonly ILogger<ShortcutHook> _logger;
    private readonly ChordDetector _detector = new();
    private readonly Channel<ChordEvent> _events = Channel.CreateUnbounded<ChordEvent>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly TimeSpan HookRefresh = TimeSpan.FromSeconds(15);

    private readonly HOOKPROC _procedure;

    /// <summary>Raised on the hook thread for every real (not injected) key press. Keep handlers instant.</summary>
    internal static event Action? UserKeyDown;
    private readonly IDisposable? _optionsChanged;
    private Thread? _thread;
    private uint _threadId;
    private Task? _dispatch;

    public ShortcutHook(CompanionSession companion, DictationSession dictation, PauseSwitch pause, IOptionsMonitor<ShortcutOptions> options, ILogger<ShortcutHook> logger)
    {
        _pause = pause;
        ArgumentNullException.ThrowIfNull(options);
        UseChords(options.CurrentValue);
        _optionsChanged = options.OnChange(UseChords);
        _companion = companion;
        _dictation = dictation;
        _logger = logger;
        _procedure = OnKeyboard; // kept in a field: the OS calls it long after construction
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _dispatch = Task.Run(DispatchAsync, CancellationToken.None);
        _thread = new Thread(RunHook) { IsBackground = true, Name = "Jarvis keyboard hook" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_threadId != 0)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, 0, 0);
        }

        _events.Writer.TryComplete();
        return _dispatch ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _optionsChanged?.Dispose();
        _events.Writer.TryComplete();
    }

    private void UseChords(ShortcutOptions options) => _detector.UseChords(options.Talk, options.Dictate);

    private void RunHook()
    {
        _threadId = PInvoke.GetCurrentThreadId();
        var hook = Install();
        if (hook.IsInvalid)
        {
            LogHookFailed(Marshal.GetLastWin32Error());
            return;
        }

        // Windows silently removes a low-level hook whose callback was ever too slow (a busy PC is
        // enough), and doesn't say so. Re-installing it regularly, new before old, keeps the
        // shortcuts alive without missing a key.
        PInvoke.SetTimer(HWND.Null, 0, (uint)HookRefresh.TotalMilliseconds, null);
        try
        {
            // Low-level hooks are delivered through this thread's message loop.
            while (PInvoke.GetMessage(out var message, HWND.Null, 0, 0) > 0)
            {
                if (message.message == PInvoke.WM_TIMER && message.hwnd.IsNull)
                {
                    var fresh = Install();
                    if (!fresh.IsInvalid)
                    {
                        hook.Dispose();
                        hook = fresh;
                    }

                    continue;
                }

                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        finally
        {
            hook.Dispose();
        }
    }

    private UnhookWindowsHookExSafeHandle Install()
        => PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _procedure, PInvoke.GetModuleHandle((string?)null), 0);

    private unsafe LRESULT OnKeyboard(int code, WPARAM wParam, LPARAM lParam)
    {
        if (code >= 0)
        {
            var key = (KBDLLHOOKSTRUCT*)lParam.Value;
            // Ignore synthetic input: our own Ctrl+V and Start-menu mask must not look like the user.
            if ((key->flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) == 0)
            {
                var isDown = (uint)wParam.Value is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
                if (isDown)
                {
                    UserKeyDown?.Invoke();
                }

                var chordEvent = _detector.OnKey((int)key->vkCode, isDown, TimeSpan.FromMilliseconds(key->time));
                // Either chord may include Win (it's configurable); the mask is harmless when it doesn't.
                if (chordEvent is { Chord: Chord.Talk or Chord.Dictate, Signal: ChordSignal.Pressed })
                {
                    MaskStartMenu();
                }

                if (chordEvent.Signal != ChordSignal.None)
                {
                    _events.Writer.TryWrite(chordEvent);
                }
            }
        }

        // Must return quickly: Windows silently removes a hook that stalls the keyboard.
        return PInvoke.CallNextHookEx(null, code, wParam, lParam);
    }

    /// <summary>Delivers events one at a time, in order, off the hook thread.</summary>
    private async Task DispatchAsync()
    {
        await foreach (var (chord, signal) in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_pause.IsPaused)
            {
                continue;
            }

            try
            {
                Dispatch(chord, signal);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // One failed action (a busy microphone, say) must not stop every shortcut after it.
                LogDispatchFailed(exception, chord, signal);
            }
        }
    }

    private void Dispatch(Chord chord, ChordSignal signal)
    {
        switch (chord, signal)
        {
            case (Chord.Talk, ChordSignal.Pressed):
                _companion.BeginListening();
                break;
            case (Chord.Talk, ChordSignal.Released):
                _companion.EndListening();
                break;
            case (Chord.Talk, ChordSignal.Cancelled):
                _companion.Cancel();
                break;
            case (Chord.Dictate, ChordSignal.Pressed):
                _dictation.BeginDictation();
                break;
            case (Chord.Dictate, ChordSignal.Released):
                _dictation.EndDictation();
                break;
            case (Chord.Dictate, ChordSignal.Cancelled):
                _dictation.Cancel();
                break;
            case (Chord.Compose, _):
                _companion.Compose();
                break;
            case (Chord.None, ChordSignal.Cancelled):
                _companion.Cancel();
                _dictation.Cancel();
                break;
        }
    }

    private static void MaskStartMenu()
    {
        Span<INPUT> inputs =
        [
            new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wVk = StartMenuMask } } },
            new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = new() { ki = new KEYBDINPUT { wVk = StartMenuMask, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP } } },
        ];
        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Chord} {Signal} shortcut failed")]
    private partial void LogDispatchFailed(Exception exception, Chord chord, ChordSignal signal);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not install the keyboard hook (error {Error}); shortcuts are unavailable")]
    private partial void LogHookFailed(int error);
}
