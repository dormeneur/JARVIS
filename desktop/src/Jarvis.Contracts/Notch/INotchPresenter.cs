namespace Jarvis.Contracts.Notch;

/// <summary>
/// The panel at the top centre of the screen. Thread-safe: calls are marshalled to the UI thread,
/// and the panel springs from whatever it is showing to the new state.
/// </summary>
public interface INotchPresenter
{
    void Show(NotchState state);

    /// <summary>What was last asked to show (so unprompted news can wait until the notch is at rest).</summary>
    NotchState Current => NotchState.Idle;
}

/// <summary>Closed set of things the notch can show.</summary>
public abstract record NotchState
{
    private NotchState()
    {
    }

    public static NotchState Idle { get; } = new IdleState();

    /// <summary>Resting: a small floating sliver under the top edge.</summary>
    public sealed record IdleState : NotchState;

    /// <summary>The microphone is open.</summary>
    public sealed record Listening : NotchState;

    /// <summary>Dictating: speech will be typed into the focused field.</summary>
    public sealed record Dictating : NotchState;

    /// <summary>A turn is being worked out. <paramref name="Caption"/> is a short note in the pill, e.g. "Looking at your screen".</summary>
    public sealed record Thinking(string? Caption = null) : NotchState;

    /// <summary>The answer is being spoken.</summary>
    public sealed record Speaking : NotchState;

    /// <summary>Background work is running; the sliver shows a moving light.</summary>
    public sealed record Working : NotchState;

    /// <summary>
    /// A text box for typing a question. The panel takes keyboard focus; Enter calls
    /// <paramref name="Submit"/> with the text, and Escape or clicking away calls <paramref name="Dismiss"/>.
    /// </summary>
    /// <param name="History">Recent exchanges, shown above the text box, oldest first.</param>
    public sealed record Composing(Action<string> Submit, Action Dismiss, IReadOnlyList<ChatTurn>? History = null) : NotchState;

    /// <summary>
    /// A written answer. <paramref name="Text"/> may grow while it streams. <paramref name="Actions"/>
    /// are buttons under it; clicking one runs it and closes the panel.
    /// </summary>
    /// <param name="KeepOnClickAway">A question waiting for an answer (approve a step): a click elsewhere doesn't close it.</param>
    public sealed record Response(string Text, IReadOnlyList<NotchAction>? Actions = null, bool KeepOnClickAway = false) : NotchState;

    /// <summary>
    /// Answers for a web form, one card per question, each with Copy. The panel takes clicks (and
    /// typing in a card) without closing when the user clicks back into the page. Showing it again
    /// with more answers keeps whatever the user already typed.
    /// </summary>
    /// <param name="Status">One short line above the cards: how many are ready, or how the filling went.</param>
    /// <param name="FillProgress">0..1 while Jarvis is filling the page in; <see langword="null"/> otherwise.</param>
    /// <param name="Fillable">Answers ready to go into the page that aren't in it yet.</param>
    /// <param name="Retry">Answers the last fill couldn't put in the page: Try again gives them another go.</param>
    public sealed record Form(string Title, IReadOnlyList<FormCard> Cards, string Status, FormActions Actions, double? FillProgress = null, int Fillable = 0, int Retry = 0) : NotchState;

    /// <summary>
    /// The notch's home, opened by resting the mouse on the sleeping notch: things to start, and
    /// what Jarvis did lately. It closes when the mouse leaves it, or with Escape.
    /// </summary>
    public sealed record Home(string Greeting, IReadOnlyList<HomeItem> Actions, IReadOnlyList<HomeItem> Recent, Action Dismiss) : NotchState;

    /// <summary>
    /// An event about to go on the user's calendar, laid out to check and edit: Add takes the
    /// fields as they are then; Cancel (or ×) adds nothing.
    /// </summary>
    /// <param name="Destination">Where it goes, in words ("Google Calendar (you@example.com)").</param>
    public sealed record EventDraft(Jarvis.Contracts.Calendar.CalendarEvent Event, string Destination, Action<Jarvis.Contracts.Calendar.CalendarEvent> Add, Action Cancel) : NotchState;
}

/// <summary>A tile or row on the notch's home. <paramref name="Glyph"/> is a Segoe Fluent Icons character.</summary>
public sealed record HomeItem(string Glyph, string Title, string? Detail, Action Invoke);

/// <summary>What the form panel's buttons do.</summary>
/// <param name="Save">After the user confirms: keep the answers they typed or changed; returns what to say.</param>
/// <param name="FillIn">Start filling the page in for the user; <see langword="null"/> while it is (or can't be).</param>
/// <param name="StopFilling">Stop filling; <see langword="null"/> unless it is.</param>
/// <param name="Used">The user picked, copied or kept this answer for the card (card id, value), so Jarvis learns it.</param>
/// <param name="Hear">Records the user for a card (its id) until the given task completes, then returns what they said (transcribed on this PC).</param>
/// <param name="Remember">Adds the words of a card's answer to the speech dictionary; returns what to say.</param>
/// <param name="Files">The resume, this form's context files and past forms, for the panel's files page.</param>
/// <param name="Reload">Answers again from the vault with the latest files: one card (its id), or the whole form (null).</param>
/// <param name="NewWords">The words of an answer that the speech dictionary doesn't know and that aren't ordinary English.</param>
public sealed record FormActions(
    Func<IReadOnlyList<FormEdit>, Task<string>> Save,
    Action Dismiss,
    Action? FillIn = null,
    Action? StopFilling = null,
    Action<string, string>? Used = null,
    Func<string, Task, Task<string>>? Hear = null,
    Func<string, string>? Remember = null,
    FormFiles? Files = null,
    Action<string?>? Reload = null,
    Func<string, IReadOnlyList<string>>? NewWords = null);

/// <summary>What the files page can do. Every call returns what to tell the user.</summary>
/// <param name="AddContext">Copies files from this PC into this form's folder in the vault, then answers again what was unknown.</param>
/// <param name="AddResume">Copies a resume into the vault (Profile/resume), then answers again what was unknown.</param>
/// <param name="Delete">Deletes a stored file or a past form's folder.</param>
public sealed record FormFiles(
    Func<Task<FormLibrary>> Load,
    Func<IReadOnlyList<string>, Task<string>> AddContext,
    Func<string, Task<string>> AddResume,
    Func<string, Task<string>> Delete);

/// <param name="History">Past forms, newest first (each a vault folder).</param>
public sealed record FormLibrary(IReadOnlyList<StoredFile> Resumes, IReadOnlyList<StoredFile> Context, IReadOnlyList<StoredFile> History);

public sealed record StoredFile(string Path, string Name, DateTimeOffset? Modified);

public enum FormCardState
{
    /// <summary>Not answered yet.</summary>
    Pending,

    /// <summary>Being drafted or looked up right now (see the note).</summary>
    Working,

    Answered,

    /// <summary>The vault doesn't say.</summary>
    Unknown,
}

/// <param name="Answer">The answer, or <see langword="null"/> while waiting or when unknown. It may grow while drafted.</param>
/// <param name="Source">Where it came from: a vault file, or "your past forms".</param>
/// <param name="Choices">Answers to pick from instead: remembered values first, then the form's options.</param>
/// <param name="Note">What is happening to it, e.g. "Writing from your vault… 12 s".</param>
/// <param name="Filled">Jarvis has put it into the page.</param>
public sealed record FormCard(string Id, string Question, string? Answer, string? Source, FormCardState State, IReadOnlyList<string>? Choices = null, string? Note = null, bool Filled = false);

/// <summary>An answer the user typed or changed, to keep in their standard answers.</summary>
public sealed record FormEdit(string Question, string Answer);

/// <summary>A button in the notch.</summary>
public sealed record NotchAction(string Label, Action Invoke);

/// <summary>One exchange in the chat shown above the text box.</summary>
public sealed record ChatTurn(string Question, string Answer);
