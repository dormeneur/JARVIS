using System.Globalization;

namespace Jarvis.Core.Cursor;

/// <summary>Parses the cursor colour setting into 0xRRGGBB; anything unrecognised falls back to blue.</summary>
public static class CursorColor
{
    public const uint Blue = 0x3380FF;
    public const uint Red = 0xFF4C4C;
    public const uint Yellow = 0xD4A017;
    public const uint Green = 0x26C281;

    public static uint Parse(string? value)
    {
        var text = value?.Trim().TrimStart('#') ?? string.Empty;
        return text.ToUpperInvariant() switch
        {
            "BLUE" => Blue,
            "RED" => Red,
            "YELLOW" => Yellow,
            "GREEN" => Green,
            _ when text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) => rgb,
            _ => Blue,
        };
    }
}
