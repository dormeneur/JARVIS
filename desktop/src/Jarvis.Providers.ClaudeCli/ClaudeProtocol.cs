using System.Text;
using System.Text.Json;
using Jarvis.Contracts.Agents;
using Jarvis.Contracts.Brain;

namespace Jarvis.Providers.ClaudeCli;

/// <summary>
/// The CLI's stream-json wire format: one JSON object per line on stdin (our messages) and stdout
/// (its events). Kept separate from process handling so it can be tested without a CLI.
/// </summary>
internal static class ClaudeProtocol
{
    /// <summary>A user message with each image preceded by its label, then the request text.</summary>
    public static string UserMessage(BrainTurn turn)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "user");
            json.WriteStartObject("message");
            json.WriteString("role", "user");
            json.WriteStartArray("content");
            foreach (var image in turn.Images)
            {
                WriteText(json, image.Label);
                json.WriteStartObject();
                json.WriteString("type", "image");
                json.WriteStartObject("source");
                json.WriteString("type", "base64");
                json.WriteString("media_type", "image/jpeg");
                json.WriteBase64String("data", image.Jpeg.Span);
                json.WriteEndObject();
                json.WriteEndObject();
            }

            WriteText(json, turn.UserText);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>Classifies one stdout line; anything we don't act on is <see cref="ClaudeOutput.Other"/>.</summary>
    public static ClaudeOutput Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            switch (Text(root, "type"))
            {
                case "stream_event"
                    when root.TryGetProperty("event", out var streamEvent)
                         && Text(streamEvent, "type") == "content_block_delta"
                         && streamEvent.TryGetProperty("delta", out var delta)
                         && Text(delta, "type") == "text_delta":
                    return new ClaudeOutput.Text(Text(delta, "text") ?? string.Empty);

                case "result":
                    var failed = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                    return new ClaudeOutput.Result(failed, Text(root, "result") ?? Text(root, "subtype") ?? string.Empty);

                default:
                    return ClaudeOutput.Other.Instance;
            }
        }
        catch (JsonException)
        {
            return ClaudeOutput.Other.Instance;
        }
    }

    /// <summary>
    /// What a non-streaming agent run reported on one stdout line: its own words, the tools it
    /// called, or the final result. Errors become an <see cref="ClaudeOutput.Result"/> with IsError.
    /// </summary>
    public static IReadOnlyList<ClaudeOutput> ParseAgent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            switch (Text(root, "type"))
            {
                case "assistant" when root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                    var events = new List<ClaudeOutput>();
                    foreach (var block in content.EnumerateArray())
                    {
                        switch (Text(block, "type"))
                        {
                            case "text" when Text(block, "text") is { Length: > 0 } text:
                                events.Add(new ClaudeOutput.Agent(new AgentEvent.Note(text)));
                                break;
                            case "tool_use" when Text(block, "name") is { } tool:
                                events.Add(new ClaudeOutput.Agent(new AgentEvent.ToolUse(tool, ToolDetail(tool, block))));
                                break;
                        }
                    }

                    return events;

                case "result":
                    var failed = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
                    return [new ClaudeOutput.Result(failed, Text(root, "result") ?? Text(root, "subtype") ?? string.Empty)];

                default:
                    return [];
            }
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The one input worth showing for each tool: the search, the site, the file.</summary>
    private static string ToolDetail(string tool, JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input))
        {
            return string.Empty;
        }

        return tool switch
        {
            "WebSearch" => Text(input, "query") ?? string.Empty,
            "WebFetch" => Uri.TryCreate(Text(input, "url"), UriKind.Absolute, out var url) ? url.Host : string.Empty,
            "Write" or "Edit" or "Read" => Path.GetFileName(Text(input, "file_path") ?? string.Empty),
            _ => string.Empty,
        };
    }

    private static void WriteText(Utf8JsonWriter json, string text)
    {
        json.WriteStartObject();
        json.WriteString("type", "text");
        json.WriteString("text", text);
        json.WriteEndObject();
    }

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal abstract record ClaudeOutput
{
    private ClaudeOutput()
    {
    }

    /// <summary>A piece of reply text.</summary>
    public sealed record Text(string Value) : ClaudeOutput;

    /// <summary>Something a background agent did (agent runs only).</summary>
    public sealed record Agent(AgentEvent Event) : ClaudeOutput;

    /// <summary>The turn finished; <paramref name="Message"/> is the full reply or the error.</summary>
    public sealed record Result(bool IsError, string Message) : ClaudeOutput;

    public sealed record Other : ClaudeOutput
    {
        public static Other Instance { get; } = new();
    }
}
