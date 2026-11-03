using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jarvis.Contracts.Browser;

namespace Jarvis.Core.Vault;

/// <summary>
/// Every answer the user has given on a form, so the next form needs the AI less and less. One
/// field per kind of question ("name", "phone", "notice period"), however a form words it, each
/// holding every distinct value seen, ranked by how often and how recently it was used. Lives in
/// the vault (<see cref="FilePath"/>), so it is backed up and synced like the rest. Not thread-safe:
/// the caller serialises access.
/// </summary>
public sealed partial class FormMemory
{
    public const string FilePath = "Profile/form-memory.json";

    /// <summary>Values kept per field; past it the least useful one goes.</summary>
    private const int MaxValues = 20;
    private const int MaxQuestions = 10;
    private const int MaxForms = 10;

    /// <summary>Two wordings this alike (shared words over all words) are the same field.</summary>
    private const double SameField = 0.75;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Common fields, however they're asked.</summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["name"] = "name", ["full name"] = "name", ["first and last name"] = "name", ["legal name"] = "name", ["candidate name"] = "name", ["applicant name"] = "name",
        ["email"] = "email", ["email address"] = "email", ["e mail"] = "email", ["e mail address"] = "email", ["mail"] = "email",
        ["phone"] = "phone", ["phone number"] = "phone", ["mobile"] = "phone", ["mobile number"] = "phone", ["contact number"] = "phone", ["telephone"] = "phone", ["cell"] = "phone", ["whatsapp number"] = "phone",
        ["country"] = "country", ["country of residence"] = "country", ["nationality"] = "nationality",
        ["city"] = "city", ["current city"] = "city", ["location"] = "city", ["current location"] = "city",
        ["address"] = "address", ["home address"] = "address", ["postal address"] = "address",
        ["linkedin"] = "linkedin", ["linkedin profile"] = "linkedin", ["linkedin url"] = "linkedin",
        ["github"] = "github", ["github profile"] = "github", ["portfolio"] = "portfolio", ["website"] = "portfolio",
        ["date of birth"] = "birth date", ["dob"] = "birth date", ["birthday"] = "birth date",
    };

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "what", "whats", "is", "are", "your", "you", "please", "enter", "provide", "the", "a", "an", "of", "do", "have", "in", "optional", "required", "current", "us",
    };

    private readonly Dictionary<string, Field> _fields;

    private FormMemory(Dictionary<string, Field> fields) => _fields = fields;

    /// <summary>Changed since it was read or last saved.</summary>
    public bool Dirty { get; private set; }

    public static FormMemory Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new FormMemory([]);
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(json, Json);
            return new FormMemory(saved?.Fields ?? []);
        }
        catch (JsonException)
        {
            return new FormMemory([]); // a hand-broken file never stops a form; it's rewritten on save
        }
    }

    public string ToJson()
    {
        Dirty = false;
        return JsonSerializer.Serialize(new Saved { Fields = _fields }, Json);
    }

    /// <summary>The field a question is about: its words without filler, or a known common field.</summary>
    public static string KeyOf(string question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var words = NotWord().Replace(question.ToLowerInvariant().Replace("'", string.Empty, StringComparison.Ordinal), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var plain = string.Join(' ', words);
        if (Synonyms.TryGetValue(plain, out var known))
        {
            return known;
        }

        var meaningful = string.Join(' ', words.Where(word => !Filler.Contains(word)));
        return Synonyms.TryGetValue(meaningful, out known) ? known : meaningful.Length > 0 ? meaningful : plain;
    }

    /// <summary>
    /// The best remembered answer the question accepts, or <see langword="null"/>. Long answers
    /// (paragraphs) are only reused on the same form: "why us?" differs per organisation.
    /// </summary>
    public RememberedAnswer? Best(FormQuestion question, string formTitle, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(question);
        var best = Ranked(question, now)
            .FirstOrDefault(entry => question.Kind != "paragraph" || entry.Forms.Contains(formTitle, StringComparer.OrdinalIgnoreCase));
        return best is null ? null : new RememberedAnswer(Fit(question, best.Value)!, best.Source);
    }

    /// <summary>Everything remembered that the question accepts, best first.</summary>
    public IReadOnlyList<string> Choices(FormQuestion question, DateTimeOffset now)
        => [.. Ranked(question, now).Select(entry => Fit(question, entry.Value)!).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Keeps a value for the question's field. <paramref name="used"/>: the user chose it (copied,
    /// picked, kept or had it filled in), which ranks it higher; otherwise it was only suggested.
    /// </summary>
    public void Remember(string question, string value, string? source, string formTitle, DateTimeOffset now, bool used)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var key = KeyOf(question);
        var field = Find(key) ?? (_fields[key] = new Field());
        if (!field.Questions.Contains(question, StringComparer.OrdinalIgnoreCase) && field.Questions.Count < MaxQuestions)
        {
            field.Questions.Add(question);
        }

        var entry = field.Values.FirstOrDefault(candidate => Same(candidate.Value, trimmed));
        if (entry is null)
        {
            entry = new Entry { Value = trimmed, Last = now, Source = source };
            field.Values.Add(entry);
            if (field.Values.Count > MaxValues)
            {
                field.Values.Remove(field.Values.MinBy(candidate => Score(candidate, now))!);
            }
        }

        if (used)
        {
            entry.Uses++;
            entry.Last = now;
            entry.Value = trimmed; // the user's latest spelling wins
        }

        entry.Source ??= source;
        if (formTitle.Length > 0 && !entry.Forms.Contains(formTitle, StringComparer.OrdinalIgnoreCase))
        {
            entry.Forms.Add(formTitle);
            if (entry.Forms.Count > MaxForms)
            {
                entry.Forms.RemoveAt(0);
            }
        }

        Dirty = true;
    }

    /// <summary>
    /// Frequency plus recency: each use counts 1, and being used lately adds up to 2 more, halving
    /// about every three weeks, so a value used once today beats one used twice last year.
    /// </summary>
    internal static double Score(Entry entry, DateTimeOffset now)
        => entry.Uses + (2 * Math.Exp(-Math.Max(0, (now - entry.Last).TotalDays) / 30));

    private IEnumerable<Entry> Ranked(FormQuestion question, DateTimeOffset now)
        => Find(KeyOf(question.Text)) is { } field
            ? field.Values.Where(entry => Fit(question, entry.Value) is not null).OrderByDescending(entry => Score(entry, now))
            : [];

    /// <summary>The field with this key, or the one worded most like it.</summary>
    private Field? Find(string key)
    {
        if (_fields.TryGetValue(key, out var exact) || _fields.Count == 0)
        {
            return exact;
        }

        var words = key.Split(' ').ToHashSet();
        var (best, similarity) = _fields
            .Select(pair => (pair.Value, Similarity: Similarity(words, pair.Key.Split(' ').ToHashSet())))
            .MaxBy(candidate => candidate.Similarity);
        return similarity >= SameField ? best : null;
    }

    private static double Similarity(HashSet<string> a, HashSet<string> b)
        => a.Count + b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();

    /// <summary>The value as this question accepts it (an offered option's own spelling), or null if it can't take it.</summary>
    private static string? Fit(FormQuestion question, string value)
    {
        if (question.Options.Count == 0 || question.Kind is not ("choice" or "dropdown" or "checkbox"))
        {
            return value;
        }

        var parts = question.Kind == "checkbox" ? value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [value.Trim()];
        var picked = parts.Select(part => question.Options.FirstOrDefault(option => Same(option, part))).ToList();
        return picked.Count > 0 && picked.All(option => option is not null) ? string.Join("; ", picked) : null;
    }

    private static bool Same(string a, string b)
        => string.Equals(string.Join(' ', a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), string.Join(' ', b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();

    private sealed class Saved
    {
        public int Version { get; set; } = 1;

        public Dictionary<string, Field> Fields { get; set; } = [];
    }

    internal sealed class Field
    {
        /// <summary>How forms have worded it.</summary>
        public List<string> Questions { get; set; } = [];

        public List<Entry> Values { get; set; } = [];
    }

    internal sealed class Entry
    {
        public string Value { get; set; } = string.Empty;

        public int Uses { get; set; }

        public DateTimeOffset Last { get; set; }

        /// <summary>The vault file it first came from, or "You" when the user gave it.</summary>
        public string? Source { get; set; }

        /// <summary>Titles of the forms it was given on.</summary>
        public List<string> Forms { get; set; } = [];
    }
}

/// <param name="Source">Where it first came from (a vault file, or "You").</param>
public sealed record RememberedAnswer(string Value, string? Source);
