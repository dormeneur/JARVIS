using System.Globalization;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Vault;

/// <summary>
/// "Add a task: renew passport": read out of the request by a plain pattern (no model, so it works
/// the same in every mode and a misread can't invent a task) and appended to one vault file the
/// user confirms first. The text only goes to the vault through jv-api, never to Claude.
/// </summary>
/// <remarks>
/// ponytail: one append-only inbox file. Ticking tasks off and "what's open" stay with the notes
/// themselves (the vault agent already lists every <c>- [ ]</c> line); add a Done flow if it's missed.
/// </remarks>
public static partial class TaskInbox
{
    /// <summary>Where tasks go, relative to the vault root. Shared with the vault agent so both append to one list.</summary>
    public const string Path = "Agent/tasks.md";

    private const int MaxLength = 300;

    /// <summary>The task in a request like "add a task: buy milk" or "add buy milk to my to-do list", else <see langword="null"/>.</summary>
    public static string? Parse(string request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var match = Leading().Match(request);
        if (!match.Success)
        {
            match = Trailing().Match(request);
        }

        if (!match.Success)
        {
            return null;
        }

        var task = Whitespace().Replace(match.Groups["task"].Value, " ").Trim().TrimEnd('.', '!', ',', ' ');
        return task.Length < 2 ? null : task.Length > MaxLength ? task[..MaxLength] : task;
    }

    /// <summary>The file's text with the task added as an unchecked item; a missing file starts with a heading.</summary>
    public static string Append(string? existing, string task, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(task);
        var line = string.Create(CultureInfo.InvariantCulture, $"- [ ] {task}  (added {today:yyyy-MM-dd})\n");
        if (string.IsNullOrWhiteSpace(existing))
        {
            return "# Tasks\n\n" + line;
        }

        return (existing.EndsWith('\n') ? existing : existing + "\n") + line;
    }

    // "add a task: buy milk", "create a to-do to call mum", "add task buy milk".
    // Anchored: "how do I add a task in Jira" is a question for Claude, not a task. Only polite openers may come first.
    [GeneratedRegex(@"^\s*(?:(?:hey|ok|okay|please|jarvis|can you|could you|would you)[,\s]+)*(?:add|put|create|save|make)\s+(?:a\s+|an\s+)?(?:new\s+)?(?:task|to-?do)(?:\s+item)?\s*(?:to|that|called|saying|for me)?\s*[:,\-]?\s*(?<task>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Leading();

    // "add buy milk to my to-do list", "put call mum on my tasks".
    [GeneratedRegex(@"^\s*(?:(?:hey|ok|okay|please|jarvis|can you|could you|would you)[,\s]+)*(?:add|put)\s+(?<task>\S.*?)\s+(?:to|on|onto|in)\s+(?:my\s+|the\s+)?(?:task|to-?do)s?(?:\s+list)?\s*[.!?]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Trailing();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
