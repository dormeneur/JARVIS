namespace Jarvis.Platform.Mac.Input;

/// <summary>macOS key codes and modifier flags, to and from the Windows virtual keys Core uses.</summary>
public static class MacKeys
{
    public const int KeyDown = 1;
    public const int KeyUp = 2;
    public const int FlagsChanged = 3;

    // CGEventFlags: device-independent masks, then the left/right device bits (IOLLEvent.h).
    public const ulong Shift = 0x20000;
    public const ulong Control = 0x40000;
    public const ulong Option = 0x80000;
    public const ulong Command = 0x100000;

    /// <summary>Any other (non-modifier) key, offset clear of every Windows key Core knows.</summary>
    private const int Other = 0x100;

    /// <summary>The modifier keys: key code → (left/right device bit, Windows left/right virtual key).</summary>
    private static readonly Dictionary<int, (ulong Bit, int VirtualKey)> ModifierKeys = new()
    {
        [59] = (0x1, 0xA2), // left control
        [62] = (0x2000, 0xA3), // right control
        [58] = (0x20, 0xA4), // left option
        [61] = (0x40, 0xA5), // right option
        [55] = (0x8, 0x5B), // left command
        [54] = (0x10, 0x5C), // right command
        [56] = (0x2, 0xA0), // left shift
        [60] = (0x4, 0xA1), // right shift
    };

    /// <summary>
    /// One tap event as the key changes it causes. A modifier arrives as "flags changed" with the
    /// new flags; whether it went down or up is its device bit. Escape is 53.
    /// </summary>
    public static IEnumerable<(int VirtualKey, bool IsDown)> Translate(int kind, int keyCode, ulong flags, ref ulong held)
    {
        if (kind == FlagsChanged)
        {
            if (!ModifierKeys.TryGetValue(keyCode, out var modifier))
            {
                return []; // caps lock, fn
            }

            var down = (flags & modifier.Bit) != 0;
            held = flags;
            return [(modifier.VirtualKey, down)];
        }

        var virtualKey = keyCode == 53 ? 0x1B : Other + keyCode;
        return [(virtualKey, kind == KeyDown)];
    }

    /// <summary>
    /// "cmd+s", "ctrl+shift+tab" → (key code, flags); Windows names are accepted too (ctrl → Command
    /// would surprise people, so Ctrl stays Control; "win" means Command). Null when a part is unknown.
    /// </summary>
    public static (int KeyCode, ulong Flags)? ParseCombination(string combination)
    {
        ArgumentNullException.ThrowIfNull(combination);
        ulong flags = 0;
        int? key = null;
        foreach (var part in combination.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    flags |= Control;
                    continue;
                case "SHIFT":
                    flags |= Shift;
                    continue;
                case "ALT" or "OPTION" or "OPT":
                    flags |= Option;
                    continue;
                case "CMD" or "COMMAND" or "WIN" or "WINDOWS" or "META":
                    flags |= Command;
                    continue;
            }

            if (key is not null || KeyCode(part) is not { } code)
            {
                return null;
            }

            key = code;
        }

        return key is { } found ? (found, flags) : null;
    }

    private static int? KeyCode(string name) => name.ToUpperInvariant() switch
    {
        "ENTER" or "RETURN" => 36,
        "TAB" => 48,
        "SPACE" => 49,
        "BACKSPACE" => 51,
        "ESC" or "ESCAPE" => 53,
        "DELETE" or "DEL" => 117,
        "HOME" => 115,
        "END" => 119,
        "PAGEUP" => 116,
        "PAGEDOWN" => 121,
        "LEFT" => 123,
        "RIGHT" => 124,
        "DOWN" => 125,
        "UP" => 126,
        var f when f.Length is 2 or 3 && f[0] == 'F' && int.TryParse(f[1..], out var n) && n is >= 1 and <= 12 => FunctionKeys[n - 1],
        var single when single.Length == 1 && Letters.IndexOf(char.ToUpperInvariant(single[0]), StringComparison.Ordinal) is var index and >= 0 => LetterCodes[index],
        _ => null,
    };

    private static readonly int[] FunctionKeys = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111];

    // ANSI layout key codes for A–Z then 0–9 (kVK_ANSI_*).
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private static readonly int[] LetterCodes =
    [
        0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6,
        29, 18, 19, 20, 21, 23, 22, 26, 28, 25,
    ];
}
