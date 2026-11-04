using Jarvis.Contracts.Notch;

namespace Jarvis.Core.Companion;

/// <summary>
/// The recent questions and answers, shown above the text box. In memory only (gone when Jarvis
/// quits) and cleared when the user asks Jarvis to forget the conversation. Thread-safe.
/// </summary>
public sealed class ConversationLog
{
    private const int Kept = 20;

    private readonly Lock _gate = new();
    private readonly List<ChatTurn> _turns = [];

    /// <summary>Raised after an exchange is added or the log is cleared. May be raised on any thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<ChatTurn> Recent
    {
        get
        {
            lock (_gate)
            {
                return [.. _turns];
            }
        }
    }

    public void Add(string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
        {
            return;
        }

        lock (_gate)
        {
            _turns.Add(new ChatTurn(question.Trim(), answer.Trim(), DateTimeOffset.Now));
            if (_turns.Count > Kept)
            {
                _turns.RemoveAt(0);
            }
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _turns.Clear();
        }

        Changed?.Invoke();
    }
}
