using System.Runtime.InteropServices;
using System.Threading.Channels;
using Jarvis.Core;
using Jarvis.Core.Companion;
using Jarvis.Core.Dictation;
using Jarvis.Core.Input;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Platform.Mac.Input;

/// <summary>
/// The global shortcuts, from a listen-only CGEvent tap (it observes keys, never swallows them).
/// macOS key codes are translated to the Windows virtual keys <see cref="ChordDetector"/> speaks,
/// so the chord rules are shared: Control+Option holds to talk (Windows' Ctrl+Alt), Control+Command
/// to dictate (Ctrl+Win), double-tap Control types, Escape cancels.
/// </summary>
public sealed partial class MacShortcutHook : IHostedService, IDisposable
{
    private readonly CompanionSession _companion;
    private readonly DictationSession _dictation;
    private readonly PauseSwitch _pause;
    private readonly ILogger<MacShortcutHook> _logger;
    private readonly ChordDetector _detector = new();
    private readonly Channel<ChordEvent> _events = Channel.CreateUnbounded<ChordEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IDisposable? _optionsChanged;
    private Task? _dispatch;
    private ulong _flags;
    private static MacShortcutHook? _current;

    /// <summary>A real (not injected) key went down: the user is typing, e.g. taking back control.</summary>
    public static event Action? UserKeyDown;

    public MacShortcutHook(CompanionSession companion, DictationSession dictation, PauseSwitch pause, IOptionsMonitor<ShortcutOptions> options, ILogger<MacShortcutHook> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _companion = companion;
        _dictation = dictation;
        _pause = pause;
        _logger = logger;
        UseChords(options.CurrentValue);
        _optionsChanged = options.OnChange(UseChords);
    }

    /// <summary>Input Monitoring is allowed, so the shortcuts work.</summary>
    public static bool Allowed => Native.InputAccess() == 1;

    public const string SettingsPage = "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _current = this;
        _dispatch = Task.Run(DispatchAsync, CancellationToken.None);
        if (!InstallTap())
        {
            LogNoAccess();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _events.Writer.TryComplete();
        return _dispatch ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _optionsChanged?.Dispose();
        _events.Writer.TryComplete();
    }

    private static unsafe bool InstallTap() => Native.TapStart(&OnKey) == 1;

    private void UseChords(ShortcutOptions options) => _detector.UseChords(options.Talk, options.Dictate);

    /// <summary>On the tap's thread: must return quickly, or macOS disables the tap.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnKey(int kind, int keyCode, ulong flags, double milliseconds)
    {
        try
        {
            _current?.Handle(kind, keyCode, flags, TimeSpan.FromMilliseconds(milliseconds));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _current?.LogKeyFailed(exception);
        }
    }

    private void Handle(int kind, int keyCode, ulong flags, TimeSpan at)
    {
        foreach (var (virtualKey, isDown) in MacKeys.Translate(kind, keyCode, flags, ref _flags))
        {
            if (isDown && kind == MacKeys.KeyDown)
            {
                UserKeyDown?.Invoke();
            }

            var chordEvent = _detector.OnKey(virtualKey, isDown, at);
            if (chordEvent.Signal != ChordSignal.None)
            {
                _events.Writer.TryWrite(chordEvent);
            }
        }
    }

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

    [LoggerMessage(Level = LogLevel.Error, Message = "The {Chord} {Signal} shortcut failed")]
    private partial void LogDispatchFailed(Exception exception, Chord chord, ChordSignal signal);

    [LoggerMessage(Level = LogLevel.Error, Message = "A key event could not be handled")]
    private partial void LogKeyFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Input Monitoring isn't allowed yet, so shortcuts are off; allow Jarvis in System Settings → Privacy & Security → Input Monitoring, then relaunch")]
    private partial void LogNoAccess();
}
