namespace Jarvis.Core.Dictation;

/// <summary>
/// Spots the user fixing a word we typed: compares the field right after insertion with the field
/// now, and if one dictated word was swapped for another, reports the swap. Only words worth
/// remembering are reported: names, brands and acronyms (they carry a capital letter or a digit,
/// so "sdk" → "SDK" counts), not ordinary lowercase spelling fixes.
/// </summary>
public static class CorrectionDetector
{
    private const int MinimumLength = 2;

    /// <param name="inserted">The text we typed.</param>
    /// <param name="afterInsert">The whole field right after it landed.</param>
    /// <param name="now">The whole field now.</param>
    public static Correction? Find(string inserted, string afterInsert, string now)
    {
        ArgumentNullException.ThrowIfNull(inserted);
        ArgumentNullException.ThrowIfNull(afterInsert);
        ArgumentNullException.ThrowIfNull(now);

        // Apps report line breaks differently (Notepad says \r, we typed \n); compare like with like.
        inserted = inserted.ReplaceLineEndings("\n");
        afterInsert = afterInsert.ReplaceLineEndings("\n");
        now = now.ReplaceLineEndings("\n");

        var start = afterInsert.IndexOf(inserted, StringComparison.Ordinal);
        if (start < 0 || afterInsert == now)
        {
            return null;
        }

        var prefix = CommonPrefix(afterInsert, now);
        var suffix = CommonSuffix(afterInsert, now, prefix);
        var changedEnd = afterInsert.Length - suffix;

        // The edit must sit inside what we typed; edits elsewhere are the user's own writing.
        if (prefix < start || changedEnd > start + inserted.Length)
        {
            return null;
        }

        var (oldStart, oldEnd) = ExpandToWord(afterInsert, prefix, changedEnd);
        var (newStart, newEnd) = ExpandToWord(now, prefix, now.Length - suffix);
        var heard = afterInsert[oldStart..oldEnd];
        var corrected = now[newStart..newEnd];

        return IsSingleWord(heard) && IsSingleWord(corrected) && IsWorthLearning(heard, corrected)
            ? new Correction(heard, corrected)
            : null;
    }

    private static bool IsWorthLearning(string heard, string corrected)
        => corrected.Length >= MinimumLength
           && !string.Equals(heard, corrected, StringComparison.Ordinal)
           && corrected.Any(char.IsLetterOrDigit)
           && (corrected.Any(char.IsUpper) || corrected.Any(char.IsDigit));

    private static bool IsSingleWord(string text) => text.Length > 0 && !text.Any(char.IsWhiteSpace);

    /// <summary>Widens [start, end) to whole words, so a fix of one letter yields the full word.</summary>
    private static (int Start, int End) ExpandToWord(string text, int start, int end)
    {
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }

        return (start, Math.Max(start, end));
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-' or '’';

    private static int CommonPrefix(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && a[length] == b[length])
        {
            length++;
        }

        return length;
    }

    private static int CommonSuffix(string a, string b, int prefix)
    {
        var length = 0;
        while (length < a.Length - prefix && length < b.Length - prefix && a[^(length + 1)] == b[^(length + 1)])
        {
            length++;
        }

        return length;
    }
}

/// <param name="Heard">What we typed.</param>
/// <param name="Corrected">What the user changed it to; the word to learn.</param>
public sealed record Correction(string Heard, string Corrected);
