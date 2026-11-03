using Jarvis.Contracts.Notch;

namespace Jarvis.Core.Notch;

/// <summary>
/// Colours of the light pooling in the notch's corner, per state. The first colour of each state is
/// the reference app's accent (DESIGN-FACTS.md §2.3); the two supporting tints are ours.
/// </summary>
public static class NotchAccent
{
    /// <summary>Three blob colours (0xRRGGBB), brightest first; <see langword="null"/> when the corner stays dark.</summary>
    public static IReadOnlyList<uint>? GlowFor(NotchState state) => state switch
    {
        NotchState.Listening => [0x66F2FF, 0x40BFFF, 0xB3E6FF],
        NotchState.Dictating => [0x21C55E, 0x1ACB4B, 0xB8FFCB],
        NotchState.Thinking => [0xFF80E6, 0xC06CFF, 0xFFC2F2],
        NotchState.Speaking => [0xFFA659, 0xFF7A45, 0xFFD2A8],
        NotchState.Response => [0x3380FF, 0x40BFFF, 0xB3E6FF],
        _ => null,
    };

    /// <summary>
    /// Whether the glow drifts and breathes. Only for the short live states: a moving gradient is
    /// re-rasterised every frame, too costly to leave running under a response the user is reading.
    /// </summary>
    public static bool GlowMoves(NotchState state)
        => state is NotchState.Listening or NotchState.Dictating or NotchState.Thinking or NotchState.Speaking;

    /// <summary>The working sweep: a mint light crossing a green-tinted bar.</summary>
    public const uint SweepTint = 0x1ACB4B;
    public const uint SweepLight = 0xB8FFCB;
}
