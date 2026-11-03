using System.Text;
using Jarvis.Contracts.Notch;

namespace Jarvis.Core.Vault;

/// <summary>
/// Profile/standard-answers.md: one "## Question" heading per answer, the layout interview mode
/// writes. New answers replace the one under the same question, or are added at the end.
/// </summary>
public static class StandardAnswers
{
    public const string FilePath = "Profile/standard-answers.md";

    public static string Merge(string? existing, IEnumerable<FormEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var lines = (existing ?? "# Standard answers\n").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
        foreach (var edit in edits)
        {
            var heading = $"## {OneLine(edit.Question)}";
            var body = edit.Answer.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n');
            var at = lines.FindIndex(line => string.Equals(line.Trim(), heading, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                lines.AddRange(["", heading, .. body]);
                continue;
            }

            var next = lines.FindIndex(at + 1, line => line.StartsWith('#'));
            var followed = next >= 0; // keep a blank line before the next heading
            lines.RemoveRange(at + 1, (followed ? next : lines.Count) - at - 1);
            lines.InsertRange(at + 1, followed ? [.. body, ""] : body);
        }

        return new StringBuilder().AppendJoin('\n', lines).Append('\n').ToString();
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('*').Trim();
}
