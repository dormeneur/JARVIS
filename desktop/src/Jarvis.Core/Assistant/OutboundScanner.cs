using System.Text.RegularExpressions;

namespace Jarvis.Core.Assistant;

/// <summary>
/// Looks for secrets in text that is about to leave this PC for Claude: API keys, private keys,
/// tokens, "password: …" and card numbers. Routing by keyword can miss a request that really
/// belonged on this PC, so this is the second line: it never decides anything itself, callers ask
/// the user, skip a step, or redact. Pure and unit-tested; findings are kinds ("card"), never text.
/// </summary>
public static partial class OutboundScanner
{
    private const string Redacted = "[redacted]";

    /// <summary>The kinds of secret in <paramref name="text"/>, in a stable order; empty when it looks clean.</summary>
    public static IReadOnlyList<string> Scan(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var kinds = new List<string>();
        if (ApiKey().IsMatch(text))
        {
            kinds.Add("api-key");
        }

        if (PrivateKey().IsMatch(text))
        {
            kinds.Add("private-key");
        }

        if (Jwt().IsMatch(text))
        {
            kinds.Add("jwt");
        }

        if (Password().IsMatch(text))
        {
            kinds.Add("password");
        }

        if (CardNumber().Matches(text).Any(match => IsCard(match.Value)))
        {
            kinds.Add("card");
        }

        return kinds;
    }

    /// <summary>The same text with every finding replaced by "[redacted]".</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = PrivateKeyBlock().Replace(text, Redacted);
        text = ApiKey().Replace(text, Redacted);
        text = Jwt().Replace(text, Redacted);
        text = Password().Replace(text, match => match.Groups["label"].Value + match.Groups["sep"].Value + Redacted);
        return CardNumber().Replace(text, match => IsCard(match.Value) ? Redacted : match.Value);
    }

    /// <summary>13 to 19 digits that pass the Luhn check: a git SHA, a UUID or a phone number won't.</summary>
    private static bool IsCard(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var doubled = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var value = digits[i] - '0';
            if (doubled)
            {
                value *= 2;
                if (value > 9)
                {
                    value -= 9;
                }
            }

            sum += value;
            doubled = !doubled;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"\bsk-(?:ant-)?[A-Za-z0-9_-]{20,}|\b(?:ghp_|gho_|ghu_|ghs_|github_pat_)[A-Za-z0-9_]{20,}|\bAKIA[0-9A-Z]{16}\b|\bAIza[0-9A-Za-z_-]{35}\b|\bxox[bpa]-[A-Za-z0-9-]{10,}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.Singleline)]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\beyJ[\w-]{10,}\.[\w-]{10,}\.[\w-]{10,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?<label>\b(?:password|passwd|pwd|passcode|pin|otp))(?<sep>\s*[:=]\s*)(?<value>\S{4,})", RegexOptions.IgnoreCase)]
    private static partial Regex Password();

    [GeneratedRegex(@"\b(?:\d[ -]?){12,18}\d\b")]
    private static partial Regex CardNumber();
}
