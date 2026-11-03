using System.Globalization;
using Jarvis.Contracts.Control;

namespace Jarvis.Core.Companion;

/// <summary>Plain words for a step, shown before the user approves it: exactly what will happen.</summary>
public static class DriveDescription
{
    private const int LongestTyped = 80;

    /// <param name="name">The page element or tab a browser step names ("e12"), in words ("the Submit button"), when known.</param>
    public static string Of(ReplyItem.Act step, Func<string, string?>? name = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        var called = name?.Invoke(step.Label) is { Length: > 0 } known ? $"\"{known}\"" : step.Label.Length > 0 ? step.Label : "the page";
        var target = string.IsNullOrEmpty(step.Label) ? "here" : $"\"{step.Label}\"";
        return step.Kind switch
        {
            ComputerActionKind.Click => $"Click {target}",
            ComputerActionKind.DoubleClick => $"Double-click {target}",
            ComputerActionKind.RightClick => $"Right-click {target}",
            ComputerActionKind.ScrollUp => $"Scroll up on {target}",
            ComputerActionKind.ScrollDown => $"Scroll down on {target}",
            ComputerActionKind.Type => $"Type \"{Shorten(step.Text ?? string.Empty)}\"",
            ComputerActionKind.Open => $"Open {Shorten(step.Text ?? string.Empty)} in your browser",
            ComputerActionKind.Press => $"Press {called} on the page",
            ComputerActionKind.Fill => $"Type \"{Shorten(step.Text ?? string.Empty)}\" into {called}",
            ComputerActionKind.Copy => step.Label == "selection" ? "Copy the selected text" : $"Copy the text of {called}",
            ComputerActionKind.Paste => $"Paste what I copied into {called}",
            ComputerActionKind.PageScroll => step.Text is "top" or "bottom" ? $"Scroll to the {step.Text} of the page" : $"Scroll the page {step.Text}",
            ComputerActionKind.TabSwitch => $"Switch to the tab {called}",
            ComputerActionKind.TabClose => step.Label.Length > 0 ? $"Close the tab {called}" : "Close this tab",
            _ => $"Press {Keys(step.Text ?? string.Empty)}",
        };
    }

    private static string Shorten(string text)
    {
        var flat = text.ReplaceLineEndings(" ⏎ ");
        return flat.Length > LongestTyped ? flat[..LongestTyped] + "…" : flat;
    }

    private static string Keys(string combination)
        => string.Join(" + ", combination.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(key => key.Length <= 1 ? key.ToUpperInvariant() : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.ToLowerInvariant())));
}
