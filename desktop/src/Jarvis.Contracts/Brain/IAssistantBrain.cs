namespace Jarvis.Contracts.Brain;

/// <summary>
/// The reasoning model behind a turn: sees the screens, reads the request, streams back text.
/// What the text means (spoken lines, pointing tags) is the app's protocol, defined by
/// <see cref="BrainTurn.Instructions"/> and parsed by the caller, so engines stay interchangeable.
/// Implementations: Claude Code CLI (free with a Pro login); a direct API can be added later.
/// </summary>
public interface IAssistantBrain
{
    /// <summary>The next turn starts a new conversation, remembering nothing said before.</summary>
    void ForgetConversation()
    {
    }

    IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, CancellationToken cancellationToken);

    /// <summary>Starts the engine ahead of the first turn so the user doesn't wait for it. Optional.</summary>
    Task PrewarmAsync(string instructions, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <param name="Instructions">System prompt; engines may keep a warm session per distinct value.</param>
/// <param name="UserText">What the user said or typed.</param>
/// <param name="Images">Pictures sent with the request, each introduced by its label, in order.</param>
public sealed record BrainTurn(string Instructions, string UserText, IReadOnlyList<LabeledImage> Images);

/// <param name="Label">Text placed right before the image, e.g. which screen it is.</param>
/// <param name="Jpeg">JPEG bytes.</param>
public sealed record LabeledImage(string Label, ReadOnlyMemory<byte> Jpeg);

/// <summary>Closed set of things a brain streams back during a turn.</summary>
public abstract record BrainEvent
{
    private BrainEvent()
    {
    }

    /// <summary>The next piece of the reply text, in order.</summary>
    public sealed record TextDelta(string Text) : BrainEvent;

    /// <summary>The reply is complete.</summary>
    public sealed record Completed : BrainEvent;
}
