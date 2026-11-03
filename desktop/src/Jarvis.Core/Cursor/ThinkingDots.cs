namespace Jarvis.Core.Cursor;

/// <summary>
/// Three dots that hop corner to corner around the cursor triangle while the brain is thinking.
/// Each beat one hop happens; dots stretch along their path mid-hop and keep their area.
/// </summary>
public static class ThinkingDots
{
    public const double DotRadius = 2.3;
    public const double CornerDistance = 8.2;

    private const double BeatSeconds = 0.72;
    private const double HopSeconds = 0.44;
    private const double MaximumStretch = 0.32;

    /// <summary>The triangle's corner angles are offset by this (radians) so dots sit on its corners.</summary>
    private const double CornerRotation = -Math.PI * 7 / 36;

    /// <summary>Dot <paramref name="index"/> (0–2) at <paramref name="seconds"/> into the loop, in DIPs from the cursor centre.</summary>
    public static DotPose Pose(int index, double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 2);

        var beat = (int)Math.Floor(seconds / BeatSeconds);
        var hop = Math.Clamp((seconds - (beat * BeatSeconds)) / HopSeconds, 0, 1);
        var from = Corner((index + beat) % 3);
        var to = Corner((index + beat + 1) % 3);
        var eased = EaseInOutCubic(hop);

        var stretch = 1 + (MaximumStretch * EaseInOutCubicSpeed(hop) / 3);
        return new DotPose(
            from.X + ((to.X - from.X) * eased),
            from.Y + ((to.Y - from.Y) * eased),
            DotRadius * stretch,
            1 / Math.Sqrt(stretch) / stretch,
            Math.Atan2(to.Y - from.Y, to.X - from.X));
    }

    private static (double X, double Y) Corner(int corner)
    {
        var angle = (-Math.PI / 2) + (corner * 2 * Math.PI / 3) + CornerRotation;
        return (Math.Cos(angle) * CornerDistance, Math.Sin(angle) * CornerDistance);
    }

    private static double EaseInOutCubic(double t)
        => t < 0.5 ? 4 * t * t * t : 1 - (Math.Pow((-2 * t) + 2, 3) / 2);

    /// <summary>Derivative of <see cref="EaseInOutCubic"/>; peaks at 3 mid-hop.</summary>
    private static double EaseInOutCubicSpeed(double t)
        => t < 0.5 ? 12 * t * t : 3 * Math.Pow((-2 * t) + 2, 2);
}

/// <param name="RadiusAlong">Radius along the direction of travel.</param>
/// <param name="AcrossScale">Multiplier for the radius across the direction of travel.</param>
/// <param name="AngleRadians">Direction of travel.</param>
public readonly record struct DotPose(double X, double Y, double RadiusAlong, double AcrossScale, double AngleRadians);
