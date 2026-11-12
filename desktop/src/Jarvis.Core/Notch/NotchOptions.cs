namespace Jarvis.Core.Notch;

/// <summary>User-facing notch settings, bound from the "Notch" settings section (Mac Settings → Notch).</summary>
public sealed class NotchOptions
{
    public const string SectionName = "Notch";

    /// <summary>Resting the mouse on the sleeping notch opens its home.</summary>
    public bool OpenOnHover { get; set; } = true;

    /// <summary>How long the mouse rests on the notch before it opens, in milliseconds (0 = at once).</summary>
    public int HoverDelayMs { get; set; } = 50;

    /// <summary>"snappy", "smooth" (the design springs as they are) or "relaxed".</summary>
    public string Speed { get; set; } = "snappy";

    /// <summary>The coloured glow in the open notch's corner.</summary>
    public bool Glow { get; set; } = true;

    /// <summary>A home opened by hovering closes when the mouse leaves it (else on a click away).</summary>
    public bool CloseOnLeave { get; set; } = true;

    /// <summary>"compact", "regular" or "large": the open notch's width.</summary>
    public string Size { get; set; } = "regular";

    /// <summary>"soft", "default" or "sharp" corners on the open notch.</summary>
    public string Corners { get; set; } = "default";

    /// <summary>"activity" (each state its own colours) or "cursor" (the cursor's colour throughout).</summary>
    public string GlowColour { get; set; } = "activity";

    /// <summary>A light trackpad tap when the notch opens.</summary>
    public bool Haptics { get; set; } = true;

    /// <summary>Files dragged to the notch are held on its shelf.</summary>
    public bool Shelf { get; set; } = true;

    public double SizeFactor => Size.ToUpperInvariant() switch { "COMPACT" => 0.85, "LARGE" => 1.15, _ => 1.0 };

    public double CornerFactor => Corners.ToUpperInvariant() switch { "SOFT" => 1.5, "SHARP" => 0.45, _ => 1.0 };

    /// <summary>Spring response multiplier for <see cref="Speed"/>; damping is left alone so nothing starts bouncing.</summary>
    public double SpeedFactor => Speed.ToUpperInvariant() switch
    {
        "SNAPPY" => 0.5,
        "RELAXED" => 1.4,
        _ => 1.0,
    };
}
