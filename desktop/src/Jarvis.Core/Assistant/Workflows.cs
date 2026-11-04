using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Assistant;

/// <summary>
/// One step of a workflow, done on the PC: "open" a link or deep link, "do" a request Jarvis does
/// by itself ("open spotify", "search lofi on youtube"), press a media "key" (playpause, next,
/// previous, volumeup, volumedown, mute), make the "volume" at least a percentage (and unmuted),
/// or "wait" some milliseconds for an app to be ready.
/// </summary>
public sealed record WorkflowStep([property: JsonPropertyName("do")] string Do, [property: JsonPropertyName("value")] string Value);

/// <summary>A phrase (or several) that runs steps, like "drop the needle".</summary>
/// <param name="Said">What Jarvis says when it runs it.</param>
public sealed record Workflow(IReadOnlyList<string> Phrases, IReadOnlyList<WorkflowStep> Steps, string Said);

/// <summary>Where taught workflows are kept (settings.json); the built-in ones come on top.</summary>
public interface IWorkflowStore
{
    IReadOnlyList<Workflow> Taught { get; }

    /// <summary>Adds it, replacing any taught one with the same phrase.</summary>
    void Save(Workflow workflow);
}

/// <summary>
/// The user's own shortcuts for everyday routines: say the phrase, the steps run on the PC with no
/// brain and no screenshot. Some come built in; "when I say X, do Y" teaches a new one (the brain
/// turns Y into steps once, then it's local every time).
/// </summary>
public static partial class Workflows
{
    public static IReadOnlyList<Workflow> BuiltIn { get; } =
    [
        new(["drop the needle", "drop the beat", "play my music", "play my liked songs"],
            [new("volume", "60"), new("open", "spotify:collection:tracks"), new("wait", "3500"), new("key", "playpause")],
            "Dropping the needle: your liked songs on Spotify."),
        new(["pause the music", "pause music", "stop the music"], [new("key", "playpause")], "Paused."),
        new(["next song", "skip this song", "skip song", "next track"], [new("key", "next")], "Next one."),
        new(["previous song", "last song", "go back a song"], [new("key", "previous")], "Going back."),
        new(["volume up", "turn it up", "louder"], [new("key", "volumeup"), new("key", "volumeup"), new("key", "volumeup")], "Louder."),
        new(["volume down", "turn it down", "quieter"], [new("key", "volumedown"), new("key", "volumedown"), new("key", "volumedown")], "Quieter."),
        new(["mute", "mute the sound", "unmute"], [new("key", "mute")], "Done."),
    ];

    /// <summary>The workflow a request asks for: its whole words match a phrase (taught ones first).</summary>
    public static Workflow? Find(string request, IEnumerable<Workflow> taught)
    {
        var said = $" {Normalize(request)} ";
        return taught.Concat(BuiltIn).FirstOrDefault(workflow => workflow.Phrases.Any(phrase => said.Contains($" {Normalize(phrase)} ", StringComparison.Ordinal) && Normalize(phrase).Length > 0 && IsMostOf(phrase, request)));
    }

    /// <summary>"When I say X, Y": the phrase and what it should do, or null when it isn't teaching.</summary>
    public static (string Phrase, string What)? Teaching(string request)
    {
        var match = Teach().Match(request.Trim());
        return match.Success ? (match.Groups["phrase"].Value.Trim(' ', '"', '\'', '“', '”', ','), match.Groups["what"].Value.Trim()) : null;
    }

    /// <summary>What the brain is told when turning a taught routine into steps; it replies with JSON only.</summary>
    public const string TeachingInstructions = """
        Turn the user's routine into steps a Windows PC runs by itself. Reply with only a JSON array, no other text.
        Each step is {"do": kind, "value": text}. Kinds:
        - "do": a request Jarvis handles itself, like "open spotify", "open bluetooth settings", "open youtube", "search lofi on youtube", "play taylor swift on spotify"
        - "open": a link or deep link (https://…, spotify:collection:tracks for liked songs, spotify:search:…, ms-settings:…, mailto:…)
        - "key": a media key: playpause, next, previous, volumeup, volumedown, mute
        - "volume": make the volume at least this percent and unmuted, e.g. "60"
        - "wait": milliseconds to let an app open before the next step, e.g. "3000"
        Only these kinds. Example for "open spotify liked songs and play": [{"do":"open","value":"spotify:collection:tracks"},{"do":"wait","value":"3500"},{"do":"key","value":"playpause"}]
        """;

    /// <summary>The brain's steps, or null when its reply isn't usable.</summary>
    public static IReadOnlyList<WorkflowStep>? ParseSteps(string reply)
    {
        var start = reply.IndexOf('[', StringComparison.Ordinal);
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var steps = JsonSerializer.Deserialize<List<WorkflowStep>>(reply[start..(end + 1)]);
            return steps is { Count: > 0 } && steps.All(step => step is not null && Kinds.Contains(step.Do) && step.Value is not null) ? steps : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> Kinds = ["do", "open", "key", "volume", "wait"];

    /// <summary>The phrase is most of what was said ("drop the needle please"), not a passing mention in a longer request.</summary>
    private static bool IsMostOf(string phrase, string request)
        => Normalize(phrase).Split(' ').Length * 2 >= Normalize(request).Split(' ').Length - 2;

    private static string Normalize(string text) => Spaces().Replace(NotWord().Replace(text.ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"^(from now on,?\s+)?(when(ever)?|if)\s+i\s+say\s+(?<phrase>.+?)\s*(,|\bthen\b|\byou should\b|\bjust\b|\bit means\b|\bmeans\b|\bi mean\b)\s*(?<what>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Teach();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
