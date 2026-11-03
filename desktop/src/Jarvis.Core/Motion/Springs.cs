namespace Jarvis.Core.Motion;

/// <summary>
/// The app's motion vocabulary. Every animation picks one of these; ad-hoc springs are a smell.
/// Values from the design spec (DESIGN-FACTS.md §1).
/// </summary>
public static class Springs
{
    /// <summary>Notch expanding to any content state.</summary>
    public static Spring NotchOpen { get; } = new(0.42, 0.82);

    /// <summary>Notch collapsing to idle; critically damped so it never bounces past the edge.</summary>
    public static Spring NotchClose { get; } = new(0.45, 1.0);

    /// <summary>Content moving inside the notch.</summary>
    public static Spring NotchContent { get; } = new(0.34, 0.86);

    /// <summary>Text composer opening.</summary>
    public static Spring TextInputOpen { get; } = new(0.20, 0.90);

    /// <summary>Buttons and small controls.</summary>
    public static Spring Control { get; } = new(0.18, 0.84);

    /// <summary>Companion cursor trailing the real mouse; slightly bouncy.</summary>
    public static Spring CursorFollow { get; } = new(0.2, 0.6);

    /// <summary>Speech bubble growing in.</summary>
    public static Spring BubbleScale { get; } = new(0.4, 0.6);

    /// <summary>Scale content starts from when it transitions in.</summary>
    public const double ContentTransitionScale = 0.92;
}
