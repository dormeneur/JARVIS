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
    public sealed record Response(string Text, IReadOnlyList<NotchAction>? Actions = null) : NotchState;
}

/// <summary>A button in the notch.</summary>
public sealed record NotchAction(string Label, Action Invoke);

/// <summary>One exchange in the chat shown above the text box.</summary>
public sealed record ChatTurn(string Question, string Answer);
