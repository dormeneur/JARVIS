using System.Runtime.CompilerServices;
using Jarvis.Contracts.Brain;
using Jarvis.Contracts.Vault;

namespace Jarvis.Core.Vault;

/// <summary>
/// Answers from the user's notes through the vault's own brain (jv-brain + Ollama, on this PC).
/// The vault server writes its own prompt, so <see cref="BrainTurn.Instructions"/> and images are
/// ignored: this brain never sees the screen.
/// </summary>
public sealed class VaultBrain(IVault vault) : IAssistantBrain
{
    public async IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        await foreach (var text in vault.AskAsync(turn.UserText, cancellationToken).ConfigureAwait(false))
        {
            yield return new BrainEvent.TextDelta(text);
        }

        yield return new BrainEvent.Completed();
    }
}

/// <summary>
/// One of two brains per turn: the local one while private mode is on, the cloud one otherwise.
/// Used where a turn has no vault data but private mode must still keep it on the PC (dictation clean-up).
/// </summary>
public sealed class PrivateModeBrain(IAssistantBrain cloud, IAssistantBrain local, Func<bool> isPrivate) : IAssistantBrain
{
    public void ForgetConversation()
    {
        cloud.ForgetConversation();
        local.ForgetConversation();
    }

    public IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, CancellationToken cancellationToken)
        => (isPrivate() ? local : cloud).RunTurnAsync(turn, cancellationToken);

    public Task PrewarmAsync(string instructions, CancellationToken cancellationToken)
        => isPrivate() ? Task.CompletedTask : cloud.PrewarmAsync(instructions, cancellationToken);
}
