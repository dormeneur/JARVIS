using System.Diagnostics;
using System.Runtime.InteropServices;
using Jarvis.Contracts.Text;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Jarvis.Platform.Text;

/// <summary>
/// Types dictated text into another app the way a person would: Ctrl+V, using a borrowed clipboard
/// that is given back afterwards. UI Automation identifies the focused field and, where the app
/// allows, reads it back to confirm the text actually landed.
/// </summary>
public sealed partial class UiaTextInsertion : ITextInsertion
{
    private const int ReadLimit = 20_000;
    private static readonly TimeSpan FocusBudget = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RestoreAfter = TimeSpan.FromMilliseconds(800);

    /// <summary>Cumulative delays at which the field is re-read after pasting.</summary>
    private static readonly int[] LandingChecksMs = [150, 150, 300, 600, 1200];

    private static readonly VIRTUAL_KEY[] Modifiers =
        [VIRTUAL_KEY.VK_CONTROL, VIRTUAL_KEY.VK_MENU, VIRTUAL_KEY.VK_SHIFT, VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_RWIN];

    private readonly ILogger<UiaTextInsertion> _logger;
    private readonly Lazy<IUIAutomation> _automation = new(() => (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(typeof(CUIAutomation8).GUID)!)!);

    public UiaTextInsertion(ILogger<UiaTextInsertion> logger)
    {
        _logger = logger;
    }

    public async Task<IFocusedField?> CaptureFocusAsync(CancellationToken cancellationToken)
    {
        // UIA calls into the target app and can stall if it is busy; never let that delay the press.
        var capture = Task.Run(CaptureFocus, cancellationToken);
        var finished = await Task.WhenAny(capture, Task.Delay(FocusBudget, cancellationToken)).ConfigureAwait(false);
        return finished == capture ? await capture.ConfigureAwait(false) : null;
    }

    public async Task<InsertOutcome> InsertAsync(IFocusedField? field, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (field is not FocusedField target)
        {
            await Task.Run(() => Clipboard.SetText(text), cancellationToken).ConfigureAwait(false);
            return InsertOutcome.CopiedToClipboard;
        }

        // The dictation chord's keys may still be down; Ctrl+V plus a held Win key would be a different shortcut.
        await WaitForModifiersReleasedAsync(cancellationToken).ConfigureAwait(false);
        if (PInvoke.GetForegroundWindow() != target.Window)
        {
            LogFocusMoved(target.AppName);
            await Task.Run(() => Clipboard.SetText(text), cancellationToken).ConfigureAwait(false);
            return InsertOutcome.CopiedToClipboard;
        }

        var before = target.CanRead ? await Task.Run(() => ReadText(target), cancellationToken).ConfigureAwait(false) : null;
        var saved = await Task.Run(Clipboard.Snapshot, cancellationToken).ConfigureAwait(false);
        await Task.Run(() => Clipboard.SetText(text), cancellationToken).ConfigureAwait(false);
        var ours = Clipboard.SequenceNumber;
        SendPaste();

        var outcome = InsertOutcome.InsertedUnverified;
        if (before is not null)
        {
            outcome = InsertOutcome.CopiedToClipboard;
            foreach (var delay in LandingChecksMs)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                var after = await Task.Run(() => ReadText(target), cancellationToken).ConfigureAwait(false);
                if (Landed(before, after, text))
                {
                    outcome = InsertOutcome.Inserted;
                    break;
                }
            }
        }

        if (outcome == InsertOutcome.CopiedToClipboard)
        {
            // It didn't land: leave the text on the clipboard so the user can paste it themselves.
            return outcome;
        }

        await Task.Delay(RestoreAfter, cancellationToken).ConfigureAwait(false);
        if (Clipboard.SequenceNumber == ours)
        {
            // Only if nothing else was copied meanwhile: never overwrite something the user copied.
            await Task.Run(() => Clipboard.Restore(saved), cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    public Task<string?> ReadAsync(IFocusedField field, CancellationToken cancellationToken)
        => field is FocusedField { CanRead: true } target
            ? Task.Run(() => ReadText(target), cancellationToken)
            : Task.FromResult<string?>(null);

    /// <summary>The field grew by roughly the inserted text (apps may reformat it slightly).</summary>
    internal static bool Landed(string before, string? after, string inserted)
        => after is not null
           && (after.Contains(inserted, StringComparison.Ordinal) || after.Length >= before.Length + (inserted.Length * 0.8));

    private FocusedField? CaptureFocus()
    {
        var window = PInvoke.GetForegroundWindow();
        if (window.IsNull)
        {
            return null;
        }

        var appName = AppNameOf(window);
        try
        {
            var element = _automation.Value.GetFocusedElement();
            var isPassword = element.CurrentIsPassword;
            var field = new FocusedField(window, appName, element, CanRead: !isPassword && SupportsReading(element));
            field.ExistingText = field.CanRead ? ReadText(field) : null;
            return field;
        }
        catch (COMException exception)
        {
            // Some apps don't expose UIA; we can still paste, just not verify.
            LogNoAutomation(appName, exception.HResult);
            return new FocusedField(window, appName, null, CanRead: false);
        }
    }

    private static bool SupportsReading(IUIAutomationElement element)
        => element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId) is not null
           || element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId) is not null;

    private static string? ReadText(FocusedField field)
    {
        if (field.Element is not { } element)
        {
            return null;
        }

        try
        {
            if (element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId) is IUIAutomationTextPattern text)
            {
                return text.DocumentRange.GetText(ReadLimit).ToString();
            }

            if (element.GetCurrentPattern(UIA_PATTERN_ID.UIA_ValuePatternId) is IUIAutomationValuePattern value)
            {
                var current = value.CurrentValue.ToString();
                return current.Length > ReadLimit ? current[..ReadLimit] : current;
            }
        }
        catch (COMException)
        {
            // The element went away (window closed, control recreated).
        }

        return null;
    }

    private static string AppNameOf(HWND window)
    {
        _ = PInvoke.GetWindowThreadProcessId(window, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var description = process.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(description) ? process.ProcessName : description;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "an app";
        }
    }

    private static async Task WaitForModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(ModifierWait.TotalSeconds * Stopwatch.Frequency);
        while (Modifiers.Any(key => (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0) && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void SendPaste()
    {
        Span<INPUT> inputs =
        [
            Key(VIRTUAL_KEY.VK_CONTROL, up: false),
            Key(VIRTUAL_KEY.VK_V, up: false),
            Key(VIRTUAL_KEY.VK_V, up: true),
            Key(VIRTUAL_KEY.VK_CONTROL, up: true),
        ];
        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(VIRTUAL_KEY key, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT { wVk = key, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0 },
        },
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Focus moved away from {App} before the text was ready; copied instead")]
    private partial void LogFocusMoved(string app);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{App} has no UI Automation (0x{HResult:X8}); pasting unverified")]
    private partial void LogNoAutomation(string app, int hResult);

    private sealed record FocusedField(HWND Window, string AppName, IUIAutomationElement? Element, bool CanRead) : IFocusedField
    {
        public string? ExistingText { get; set; }
    }
}
