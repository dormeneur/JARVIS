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
        NotchState.Response or NotchState.Form or NotchState.Home or NotchState.EventDraft or NotchState.Settings => [0x3380FF, 0x40BFFF, 0xB3E6FF],
        _ => null,
    };

    /// <summary>
    /// Whether the glow drifts and breathes. Only for the short live states: a moving gradient is
    /// re-rasterised every frame, too costly to leave running under a response the user is reading.
    /// </summary>
    public static bool GlowMoves(NotchState state)
        => state is NotchState.Listening or NotchState.Dictating or NotchState.Thinking or NotchState.Speaking;

    /// <summary>
    /// How tall the glow reaches, in DIPs; <see langword="null"/> for the spec's own (it scales with the
    /// panel's height, DESIGN-FACTS.md §2.3). Panels people read and work in for a while (form cards,
    /// home) keep a short reply's glow tucked into the corner, so the light stays behind the edge and
    /// not under the text; they aren't in the reference app.
    /// </summary>
    public static double? GlowReach(NotchState state) => state is NotchState.Form or NotchState.Home or NotchState.EventDraft or NotchState.Settings ? 96 : null;

    /// <summary>The glow's strength, 0..1 of its usual opacity (dimmer behind reading panels; ours).</summary>
    public static double GlowStrength(NotchState state) => state is NotchState.Settings ? 0.4 : state is NotchState.Form or NotchState.Home or NotchState.EventDraft ? 0.7 : 1;

    /// <summary>The working sweep: a mint light crossing a green-tinted bar.</summary>
    public const uint SweepTint = 0x1ACB4B;
    public const uint SweepLight = 0xB8FFCB;
}
