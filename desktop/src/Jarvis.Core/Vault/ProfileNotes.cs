using Jarvis.Contracts.Vault;

namespace Jarvis.Core.Vault;

/// <summary>
/// What Jarvis has learned about the user, as the notch's "About me" page shows it: every Markdown
/// file in Profile/, each "- " line one fact the user can edit or delete. Everything else in a file
/// (headings, paragraphs) is kept as it is. An edit replaces the exact line it was made on, so a fact
/// the brain appended meanwhile (a new hunch answer, a phone edit) is never lost.
/// </summary>
public static class ProfileNotes
{
    public const string Folder = "Profile";
    public const string Traits = "Profile/traits.md";
    public const string NotMe = "Profile/not-me.md";
    private const int MaxFact = 500;

    /// <summary>Written by the brain on every change (its open guesses): not facts, and edits there would be overwritten.</summary>
    private static readonly string[] Hidden = ["hunches.md"];

    /// <summary>Folders in Profile/ that belong to other features (forms, resumes, documents): not knowledge to edit here.</summary>
    public static readonly IReadOnlySet<string> OtherFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "forms", "resume", "documents" };

    /// <summary>
    /// A new topic's file from what the user typed: "Travel" → Profile/travel.md, "people/family" →
    /// Profile/people/family.md (one folder deep). Null when nothing usable is left.
    /// </summary>
    public static string? TopicPath(string name)
    {
        var parts = (name ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Slug).Where(part => part.Length > 0).ToList();
        if (parts.Count == 0 || parts.Count > 2 || (parts.Count == 2 && OtherFolders.Contains(parts[0])))
        {
            return null;
        }

        var file = parts[^1].EndsWith("-md", StringComparison.Ordinal) ? parts[^1][..^3] : parts[^1];
        return $"{Folder}/{(parts.Count == 2 ? parts[0] + "/" : string.Empty)}{file}.md";
    }

    private static string Slug(string part)
    {
        var slug = new string([.. part.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }

    /// <summary>The Profile files to show, the learned traits first.</summary>
    public static IReadOnlyList<string> Files(IEnumerable<VaultEntry> entries)
        => [.. (entries ?? []).Where(entry => !entry.IsFolder && entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !Hidden.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            .Select(entry => entry.Path)
            .OrderBy(path => path == Traits ? 0 : path == NotMe ? 2 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A heading for a file: "What Jarvis knows about you" for traits.md, else its name ("work.md" → "Work").</summary>
    public static string Title(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path == Traits)
        {
            return "What Jarvis knows about you";
        }

        if (path == NotMe)
        {
            return "Wrong guesses (never asked again)";
        }

        static string Words(string part) => part.Replace('-', ' ').Replace('_', ' ') is { Length: > 0 } words ? char.ToUpperInvariant(words[0]) + words[1..] : part;
        var inFolder = path.StartsWith(Folder + "/", StringComparison.Ordinal) ? path[(Folder.Length + 1)..] : path;
        var folder = inFolder.Contains('/', StringComparison.Ordinal) ? Words(inFolder[..inFolder.IndexOf('/', StringComparison.Ordinal)]) + " / " : string.Empty;
        var name = Words(Path.GetFileNameWithoutExtension(path));
        return name.Length == 0 ? path : folder + name;
    }

    /// <summary>
    /// Shown on "About me": a file of facts, the traits file, or a new topic (nothing but headings yet).
    /// Pages of paragraphs (standard answers) aren't facts to edit line by line.
    /// </summary>
    public static bool IsKnowledge(string path, string? text)
        => path == Traits || Facts(text).Count > 0 || Lines(text).All(line => line.Trim().Length == 0 || line.TrimStart().StartsWith('#'));

    /// <summary>The facts in a file: its "- " lines, without the dash.</summary>
    public static IReadOnlyList<string> Facts(string? text)
        => [.. Lines(text).Where(IsFact).Select(line => line.TrimStart()[2..].Trim()).Where(fact => fact.Length > 0)];

    /// <summary>
    /// The file with one fact changed (or removed, when <paramref name="replacement"/> is null or
    /// blank); null when that fact isn't in the file any more (changed elsewhere: reload).
    /// </summary>
    public static string? Replace(string? text, string fact, string? replacement)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var lines = Lines(text).ToList();
        var at = lines.FindIndex(line => IsFact(line) && line.TrimStart()[2..].Trim() == fact.Trim());
        if (at < 0)
        {
            return null;
        }

        var clean = Clean(replacement);
        if (clean.Length == 0)
        {
            lines.RemoveAt(at);
        }
        else
        {
            var indent = lines[at][..(lines[at].Length - lines[at].TrimStart().Length)];
            lines[at] = $"{indent}- {clean}";
        }

        return Join(lines);
    }

    /// <summary>The file with a new fact at the end; a new file starts with its heading.</summary>
    public static string Add(string? text, string fact, string path)
    {
        var clean = Clean(fact);
        if (clean.Length == 0)
        {
            return text ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return $"# {Title(path)}\n\n- {clean}\n";
        }

        return (text.EndsWith('\n') ? text : text + "\n") + $"- {clean}\n";
    }

    /// <summary>One line (a fact never spans lines), at most 500 characters.</summary>
    private static string Clean(string? fact)
    {
        var line = string.Join(' ', (fact ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > MaxFact ? line[..MaxFact] : line;
    }

    private static bool IsFact(string line) => line.TrimStart().StartsWith("- ", StringComparison.Ordinal);

    private static IEnumerable<string> Lines(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Join(List<string> lines) => string.Join('\n', lines);
}
