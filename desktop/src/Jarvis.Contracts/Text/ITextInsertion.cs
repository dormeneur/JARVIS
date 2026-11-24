namespace Jarvis.Contracts.Text;

/// <summary>
/// Puts dictated text into whatever the user was typing in. The field is remembered when the key
/// goes down, because by the time the text is ready focus may have moved.
/// </summary>
public interface ITextInsertion
{
    /// <summary>The focused text field right now, or <see langword="null"/> if focus is not in a text field.</summary>
    Task<IFocusedField?> CaptureFocusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts <paramref name="text"/> at the caret of <paramref name="field"/>, leaving the user's
    /// clipboard as it was. Falls back to copying the text when it cannot be placed.
    /// </summary>
    Task<InsertOutcome> InsertAsync(IFocusedField? field, string text, CancellationToken cancellationToken);

    /// <summary>
    /// The field's whole text now, or <see langword="null"/> if it can't be read (gone, password,
    /// app without UI Automation). Used to notice the user correcting dictated words.
    /// </summary>
    Task<string?> ReadAsync(IFocusedField field, CancellationToken cancellationToken);

    /// <summary>
    /// The text selected in the app in front, or null when nothing is (or it can't be read). Never
    /// from a password field; the clipboard, if borrowed to copy it, is given back.
    /// </summary>
    Task<TextSelection?> ReadSelectionAsync(CancellationToken cancellationToken) => Task.FromResult<TextSelection?>(null);
}

/// <summary>Text the user selected, and where: the app (or page title) and, in a browser, the page's address.</summary>
public sealed record TextSelection(string Text, string Source, string? Url = null);

/// <summary>What the user asked Jarvis to do with a selection.</summary>
public enum SelectionAction
{
    /// <summary>Show the choices (double-tap Ctrl with text selected).</summary>
    Menu,
    Explain,
    Summarise,

    /// <summary>Keep it in the vault, filed where it belongs.</summary>
    Save,
}

/// <summary>A text field captured by <see cref="ITextInsertion.CaptureFocusAsync"/>.</summary>
public interface IFocusedField
{
    /// <summary>The app's friendly name, e.g. "Notepad" or "Slack".</summary>
    string AppName { get; }

    /// <summary>Text already in the field (never read from password fields); helps clean-up match style and names.</summary>
    string? ExistingText { get; }
}

public enum InsertOutcome
{
    /// <summary>The text is in the field and reading it back confirmed it.</summary>
    Inserted,

    /// <summary>The text was pasted, but this app does not let us read the field back.</summary>
    InsertedUnverified,

    /// <summary>It could not be placed (focus moved, no field); the text is on the clipboard.</summary>
    CopiedToClipboard,
}
