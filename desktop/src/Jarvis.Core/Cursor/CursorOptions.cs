namespace Jarvis.Core.Cursor;

/// <summary>User-facing cursor settings, bound from the "Cursor" settings section.</summary>
public sealed class CursorOptions
{
    public const string SectionName = "Cursor";

    /// <summary>Six-digit hex (with or without '#') or one of: blue, red, yellow, green.</summary>
    public string Color { get; set; } = "#3380FF";

    /// <summary>When off, the floating cursor is hidden and does not follow the mouse.</summary>
    public bool Visible { get; set; } = true;

    public bool ShakeToDetach { get; set; } = true;

    /// <summary>Step aside (cursor and resting notch) while a game, video or presentation is fullscreen.</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>While the talk keys are held, moving the mouse draws a highlighter trail Jarvis reads as "this".</summary>
    public bool Highlighter { get; set; } = true;

    /// <summary>How closely it trails the mouse: "instant", "fast" or "natural" (the bouncy design spring).</summary>
    public string FollowSpeed { get; set; } = "fast";

    /// <summary>The spring for <see cref="FollowSpeed"/>; <see langword="null"/> means no trailing.</summary>
    public Motion.Spring? FollowSpring => FollowSpeed.ToUpperInvariant() switch
    {
        "INSTANT" => null,
        "NATURAL" => Motion.Springs.CursorFollow,
        _ => new Motion.Spring(0.08, 0.85),
    };

    /// <summary>The floating cursor's size, 1 = the design size (0.5 to 2).</summary>
    public double Size { get; set; } = 1.0;
}
