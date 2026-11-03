namespace Jarvis.Core.Input;

/// <summary>Which modifier pairs are held to talk and to dictate, bound from the "Shortcuts" settings section.</summary>
public sealed class ShortcutOptions
{
    public const string SectionName = "Shortcuts";

    /// <summary>
    /// The pairs offered. Alt+Shift (and Ctrl+Shift on some keyboards) switch the input language,
    /// so Alt+Shift isn't offered.
    /// </summary>
    public static IReadOnlyList<string> HoldChoices { get; } = ["Ctrl + Alt", "Ctrl + Win", "Ctrl + Shift", "Alt + Win", "Shift + Win"];

    public string Talk { get; set; } = "Ctrl + Alt";

    public string Dictate { get; set; } = "Ctrl + Win";
}
