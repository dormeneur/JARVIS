using System.Globalization;
using Jarvis.Contracts.Control;

namespace Jarvis.Core.Companion;

/// <summary>Plain words for a step, shown before the user approves it: exactly what will happen.</summary>
public static class DriveDescription
{
    private const int LongestTyped = 80;

    public static string Of(ReplyItem.Act step)
    {
        ArgumentNullException.ThrowIfNull(step);
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
