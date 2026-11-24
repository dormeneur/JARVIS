using System.Text.Json;
using Jarvis.Contracts.Notch;

namespace Jarvis.Core.Companion;

/// <summary>
/// The recent questions and answers, shown above the text box. Kept in a file when given one, so the
/// chat is still there after a restart; cleared when the user asks Jarvis to forget the conversation.
/// Thread-safe.
/// </summary>
public sealed class ConversationLog
{
    private const int Kept = 20;

    private readonly Lock _gate = new();
    private readonly List<ChatTurn> _turns = [];
    private readonly string? _file;

    /// <param name="file">Where the chat is kept between runs (on this PC); null keeps it in memory only.</param>
    public ConversationLog(string? file = null)
    {
        _file = file;
        if (file is not null && File.Exists(file))
        {
            try
            {
                _turns.AddRange((JsonSerializer.Deserialize<List<ChatTurn>>(File.ReadAllText(file)) ?? []).TakeLast(Kept));
            }
            catch (JsonException)
            {
                // A damaged file just means an empty chat: it's rewritten on the next exchange.
            }
        }
    }

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

            SaveLocked();
        }

        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _turns.Clear();
            SaveLocked();
        }

        Changed?.Invoke();
    }

    private void SaveLocked()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_turns));
            File.Move(temporary, _file, overwrite: true); // atomic: a crash never leaves half a chat
        }
        catch (IOException)
        {
            // The chat still works; it just won't survive this restart.
        }
    }
}
