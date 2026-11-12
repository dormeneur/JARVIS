using System.Diagnostics;
using Jarvis.Contracts.Text;
using Jarvis.Platform.Mac.Input;
using Microsoft.Extensions.Logging;

namespace Jarvis.Platform.Mac.Text;

/// <summary>
/// Dictation's hands, as on Windows: the Accessibility API remembers the focused field and its
/// text (never a password field's), then the text is pasted with Command+V and the field re-read
/// to confirm it landed. The pasteboard is put back after 0.8 s, only if nothing else was copied.
/// Without Accessibility permission the text is left on the pasteboard.
/// </summary>
public sealed partial class MacTextInsertion : ITextInsertion
{
    private const int ReadLimit = 20_000;
    private const int VKey = 9;
    private static readonly TimeSpan FocusBudget = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RestoreAfter = TimeSpan.FromMilliseconds(800);
    private static readonly int[] LandingChecksMs = [150, 150, 300, 600, 1200];

    private readonly ILogger<MacTextInsertion> _logger;

    public MacTextInsertion(ILogger<MacTextInsertion> logger)
    {
        _logger = logger;
    }

    public const string SettingsPage = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    /// <summary>Accessibility is allowed (needed to read fields, paste and drive).</summary>
    public static bool Allowed => Native.AxTrusted(0) == 1;

    public static void AskForAccess() => Native.AxTrusted(1);

    public async Task<IFocusedField?> CaptureFocusAsync(CancellationToken cancellationToken)
    {
        // The target app answers Accessibility calls; a busy one must not delay the press.
        var capture = Task.Run(CaptureFocus, cancellationToken);
        var finished = await Task.WhenAny(capture, Task.Delay(FocusBudget, cancellationToken)).ConfigureAwait(false);
        return finished == capture ? await capture.ConfigureAwait(false) : null;
    }

    public async Task<InsertOutcome> InsertAsync(IFocusedField? field, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (field is not FocusedField target || !Allowed)
        {
            Native.PasteboardSet(text, transient: 0);
            return InsertOutcome.CopiedToClipboard;
        }

        // The dictation chord's keys may still be down; Command+V with Control held is another shortcut.
        await WaitForModifiersReleasedAsync(cancellationToken).ConfigureAwait(false);
        if (Native.AxIsFocused(target.Handle) == 0)
        {
            LogFocusMoved(target.AppName);
            Native.PasteboardSet(text, transient: 0);
            return InsertOutcome.CopiedToClipboard;
        }

        var before = target.CanRead ? Read(target) : null;
        var saved = Native.PasteboardSave();
        var restore = false;
        try
        {
            var ours = Native.PasteboardSet(text, transient: 1);
            Native.PostKey(VKey, MacKeys.Command);

            var outcome = InsertOutcome.InsertedUnverified;
            if (before is not null)
            {
                outcome = InsertOutcome.CopiedToClipboard;
                foreach (var delay in LandingChecksMs)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    if (Landed(before, Read(target), text))
                    {
                        outcome = InsertOutcome.Inserted;
                        break;
                    }
                }
            }

            if (outcome == InsertOutcome.CopiedToClipboard)
            {
                // It didn't land: leave the text on the pasteboard so the user can paste it themselves.
                Native.PasteboardSet(text, transient: 0);
                return outcome;
            }

            await Task.Delay(RestoreAfter, cancellationToken).ConfigureAwait(false);
            restore = Native.PasteboardChangeCount() == ours; // never overwrite something the user copied
            return outcome;
        }
        finally
        {
            Native.PasteboardRestore(saved, restore ? 1 : 0);
        }
    }

    public Task<string?> ReadAsync(IFocusedField field, CancellationToken cancellationToken)
        => field is FocusedField { CanRead: true } target
            ? Task.Run(() => Read(target), cancellationToken)
            : Task.FromResult<string?>(null);

    internal static bool Landed(string before, string? after, string inserted)
        => after is not null
           && (after.Contains(inserted, StringComparison.Ordinal) || after.Length >= before.Length + (inserted.Length * 0.8));

    private static FocusedField? CaptureFocus()
    {
        if (!Allowed)
        {
            return null;
        }

        var handle = Native.AxFocused(out var app, out var text, out var secure);
        if (handle == 0)
        {
            return null;
        }

        var existing = Native.Take(text);
        var field = new FocusedField(handle, Native.Take(app) ?? "an app", CanRead: secure == 0 && existing is not null)
        {
            ExistingText = Limit(existing),
        };
        return field;
    }

    private static string? Read(FocusedField field) => Limit(Native.Take(Native.AxRead(field.Handle)));

    private static string? Limit(string? text) => text is { Length: > ReadLimit } ? text[..ReadLimit] : text;

    private static async Task WaitForModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        const ulong any = MacKeys.Control | MacKeys.Option | MacKeys.Command | MacKeys.Shift;
        var deadline = Stopwatch.GetTimestamp() + (long)(ModifierWait.TotalSeconds * Stopwatch.Frequency);
        while ((Native.Modifiers() & any) != 0 && Stopwatch.GetTimestamp() < deadline)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Focus moved away from {App} before the text was ready; copied instead")]
    private partial void LogFocusMoved(string app);

    /// <summary>A retained AXUIElement, released when the field is collected.</summary>
    private sealed record FocusedField(nint Handle, string AppName, bool CanRead) : IFocusedField
    {
        public string? ExistingText { get; init; }

        ~FocusedField() => Native.AxRelease(Handle);
    }
}
