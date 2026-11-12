namespace Jarvis.Platform.Mac.Text;

/// <summary>Whether a word is English (US or UK), by the macOS spell checker: "Add to dictionary" offers only the rest.</summary>
public static class EnglishWords
{
    public static bool IsKnown(string word) => !string.IsNullOrWhiteSpace(word) && Native.SpellKnown(word) == 1;
}
