using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Jarvis.Contracts.Brain;
using Microsoft.Extensions.Options;

namespace Jarvis.Providers.Local;

/// <summary>Bound from the "Brain:Ollama" settings section.</summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Brain:Ollama";

    /// <summary>Ollama on this PC.</summary>
    public string Address { get; set; } = "http://127.0.0.1:11434";

    /// <summary>A text model already pulled into that Ollama.</summary>
    public string Model { get; set; } = "qwen3:8b";

    /// <summary>After this much quiet, the next turn starts a fresh conversation.</summary>
    public TimeSpan FreshConversationAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Context window sent as <c>num_ctx</c>. Ollama's own default silently cuts long prompts from
    /// the front, and a value that differs from other callers of the same model makes it reload,
    /// so keep this equal to the brain's <c>JARVIS_NUM_CTX</c> when both use one model.
    /// </summary>
    public int ContextLength { get; set; } = 8192;
}

/// <summary>
/// The brain on this PC: a model in the user's own Ollama, for private mode. Text only (images in
/// a turn are ignored), thinking off so spoken replies start quickly, and a short conversation
/// memory kept in this process.
/// </summary>
public sealed class OllamaBrain : IAssistantBrain
{
    private const int RememberedTurns = 10;

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<OllamaOptions> _options;
    private readonly TimeProvider _time;
    private readonly bool _remember;
    private readonly List<JsonObject> _history = [];
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private DateTimeOffset _lastUsed;

    /// <param name="remember">False: every turn stands alone (dictation clean-up).</param>
    public OllamaBrain(HttpClient http, IOptionsMonitor<OllamaOptions> options, TimeProvider time, bool remember = true)
    {
        _http = http;
        _options = options;
        _time = time;
        _remember = remember;
    }

    public void ForgetConversation()
    {
        lock (_history)
        {
            _history.Clear();
        }
    }

    public async IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var options = _options.CurrentValue;
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_remember || _time.GetUtcNow() - _lastUsed > options.FreshConversationAfter)
            {
                ForgetConversation();
            }

            _lastUsed = _time.GetUtcNow();
            var question = new JsonObject { ["role"] = "user", ["content"] = turn.UserText };
            JsonArray messages;
            lock (_history)
            {
                messages = [new JsonObject { ["role"] = "system", ["content"] = turn.Instructions }, .. _history.Select(message => message.DeepClone()), question.DeepClone()];
            }

            var body = new JsonObject { ["model"] = options.Model, ["messages"] = messages, ["stream"] = true, ["think"] = false, ["options"] = new JsonObject { ["num_ctx"] = options.ContextLength } };
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Address.TrimEnd('/') + "/api/chat") { Content = JsonContent.Create(body) };
            using var response = await SendAsync(request, options, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            var reply = new StringBuilder();
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (JvApiVault.Parse(line) is not { } chunk)
                {
                    continue;
                }

                if (chunk["error"] is { } error)
                {
                    throw new InvalidOperationException($"The local model failed: {error}");
                }

                if (chunk["message"]?["content"]?.GetValue<string>() is { Length: > 0 } text)
                {
                    reply.Append(text);
                    yield return new BrainEvent.TextDelta(text);
                }

                if (chunk["done"]?.GetValue<bool>() == true)
                {
                    break;
                }
            }

            lock (_history)
            {
                _history.Add(question);
                _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply.ToString() });
                if (_history.Count > RememberedTurns * 2)
                {
                    _history.RemoveRange(0, _history.Count - (RememberedTurns * 2));
                }
            }

            yield return new BrainEvent.Completed();
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, OllamaOptions options, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"Ollama isn't running at {options.Address}. Start Ollama, then ask again.", exception);
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new InvalidOperationException($"The local model ({options.Model}) failed: {detail}");
        }

        return response;
    }
}
