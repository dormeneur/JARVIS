namespace Jarvis.Core.Cursor;

/// <summary>
/// The cursor while the microphone is open: an audio meter of 5 bars, 2 DIPs wide, 2 apart, corner
/// radius 1.5 (DESIGN-FACTS.md §3). The middle bar is tallest, so it reads as a voice, not a chart.
/// </summary>
public static class ListeningBars
{
    public const int Count = 5;
    public const double BarWidth = 2;
    public const double Gap = 2;
    public const double CornerRadius = 1.5;

    private const double MinimumHeight = 3;
    private const double MaximumHeight = 16;
    private static readonly double[] Profile = [0.55, 0.8, 1, 0.8, 0.55];

    /// <summary>Height of bar <paramref name="index"/> in DIPs for a 0–1 level.</summary>
    public static double Height(int index, double level)
        => MinimumHeight + ((MaximumHeight - MinimumHeight) * Math.Clamp(level, 0, 1) * Profile[index]);
}
