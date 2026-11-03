namespace Jarvis.Core.Notch;

/// <summary>
/// Resting the mouse on the sleeping notch opens it: a ring fills beside the cursor for
/// <see cref="Hold"/>, then the notch's home opens. Moving away resets it; after it opens, the
/// mouse must leave the notch before it can fire again. Feed it every frame.
/// </summary>
public sealed class NotchDwell
{
    /// <summary>How long to rest on the notch (DESIGN-FACTS.md §3, notch hover dwell).</summary>
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(0.45);

    /// <summary>The ring fills in this many steps (DESIGN-FACTS.md §3, dwell ring).</summary>
    public const int Steps = 32;

    /// <summary>How far around the sliver still counts as "on" it, in DIPs (ours: the sliver alone is a tiny target).</summary>
    public const double Reach = 24;

    private TimeSpan? _since;
    private bool _armed = true;

    /// <summary>Whether the pointer is on (or right around) the resting sliver at the top centre of the primary display.</summary>
    /// <param name="scale">The primary display's scale; positions are physical pixels.</param>
    public static bool OnSliver(double x, double y, double displayLeft, double displayTop, double displayWidth, double scale)
        => Math.Abs(x - (displayLeft + (displayWidth / 2))) <= ((NotchLayout.SliverWidth / 2) + Reach) * scale
           && y >= displayTop
           && y - displayTop <= (NotchLayout.SliverTopGap + NotchLayout.SliverHeight + Reach) * scale;

    /// <param name="allowed">The notch is resting and Jarvis isn't paused, busy or behind a fullscreen app.</param>
    /// <returns>The ring's fill (0..1, in steps) while resting there, else <see langword="null"/>; and whether the hold just completed.</returns>
    public (double? Progress, bool Opened) Update(TimeSpan now, bool onSliver, bool allowed)
    {
        if (!onSliver)
        {
            (_since, _armed) = (null, true);
            return (null, false);
        }

        if (!allowed || !_armed)
        {
            _since = null;
            return (null, false);
        }

        _since ??= now;
        var done = (now - _since.Value) / Hold;
        if (done >= 1)
        {
            (_since, _armed) = (null, false);
            return (null, true);
        }

        return (Math.Floor(done * Steps) / Steps, false);
    }
}
