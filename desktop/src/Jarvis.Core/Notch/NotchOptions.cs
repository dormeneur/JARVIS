namespace Jarvis.Core.Notch;

/// <summary>User-facing notch settings, bound from the "Notch" settings section (Mac Settings → Notch).</summary>
public sealed class NotchOptions
{
    public const string SectionName = "Notch";

    /// <summary>Resting the mouse on the sleeping notch opens its home.</summary>
    public bool OpenOnHover { get; set; } = true;

    /// <summary>How long the mouse rests on the notch before it opens, in milliseconds (0 = at once).</summary>
    public int HoverDelayMs { get; set; } = 150;

    /// <summary>"snappy", "smooth" (the design springs as they are) or "relaxed".</summary>
    public string Speed { get; set; } = "snappy";

    /// <summary>The coloured glow in the open notch's corner.</summary>
    public bool Glow { get; set; } = true;

    /// <summary>Spring response multiplier for <see cref="Speed"/>; damping is left alone so nothing starts bouncing.</summary>
    public double SpeedFactor => Speed.ToUpperInvariant() switch
    {
        "SNAPPY" => 0.6,
        "RELAXED" => 1.4,
        _ => 1.0,
    };
}
