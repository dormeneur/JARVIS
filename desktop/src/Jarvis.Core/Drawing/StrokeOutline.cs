using System.Numerics;
using Jarvis.Contracts.Overlay;

namespace Jarvis.Core.Drawing;

/// <summary>
/// Turns a pen path into the outline of an ink stroke: thicker where the pen presses or slows,
/// tapered at both ends, with round caps. Same idea as the perfect-freehand library (MIT), written
/// for this app. The result is a closed polygon to fill.
/// </summary>
public static class StrokeOutline
{
    /// <summary>Points closer than this (relative to stroke size) are merged; avoids spiky outlines.</summary>
    private const float MinimumSpacing = 0.1f;
    private const int CapSegments = 8;

    /// <param name="path">Pen positions in order; at least one.</param>
    /// <param name="size">Full stroke width at full pressure.</param>
    /// <param name="thinning">0 = constant width, 1 = width fully follows pressure.</param>
    /// <param name="taper">Distance over which each end narrows to a point (0 = no taper).</param>
    public static IReadOnlyList<Vector2> Build(IReadOnlyList<InkPoint> path, float size, float thinning = 0.55f, float taper = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            return [];
        }

        var points = Resample(path, size * MinimumSpacing);
        if (points.Count == 1)
        {
            return Circle(points[0].Position, Radius(points[0].Pressure, size, thinning));
        }

        var total = 0f;
        var travelled = new float[points.Count];
        for (var i = 1; i < points.Count; i++)
        {
            total += Vector2.Distance(points[i - 1].Position, points[i].Position);
            travelled[i] = total;
        }

        var left = new List<Vector2>(points.Count);
        var right = new List<Vector2>(points.Count);
        var radii = new float[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var radius = Radius(points[i].Pressure, size, thinning) * TaperFactor(travelled[i], total, taper);
            radii[i] = radius;
            var direction = Direction(points, i);
            var normal = new Vector2(-direction.Y, direction.X) * radius;
            left.Add(points[i].Position + normal);
            right.Add(points[i].Position - normal);
        }

        var outline = new List<Vector2>((points.Count * 2) + (CapSegments * 2));
        outline.AddRange(left);
        AddCap(outline, points[^1].Position, Direction(points, points.Count - 1), radii[^1]);
        for (var i = right.Count - 1; i >= 0; i--)
        {
            outline.Add(right[i]);
        }

        AddCap(outline, points[0].Position, -Direction(points, 0), radii[0]);
        return outline;
    }

    /// <summary>The first <paramref name="fraction"/> of a path by length, for drawing it as if the pen were still moving.</summary>
    public static IReadOnlyList<InkPoint> Prefix(IReadOnlyList<InkPoint> path, float fraction)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (fraction >= 1 || path.Count < 2)
        {
            return path;
        }

        var total = 0f;
        for (var i = 1; i < path.Count; i++)
        {
            total += Vector2.Distance(path[i - 1].Position, path[i].Position);
        }

        var target = total * Math.Max(0, fraction);
        var prefix = new List<InkPoint> { path[0] };
        var walked = 0f;
        for (var i = 1; i < path.Count; i++)
        {
            var step = Vector2.Distance(path[i - 1].Position, path[i].Position);
            if (walked + step >= target)
            {
                var t = step == 0 ? 0 : (target - walked) / step;
                prefix.Add(new InkPoint(Vector2.Lerp(path[i - 1].Position, path[i].Position, t), path[i].Pressure));
                return prefix;
            }

            walked += step;
            prefix.Add(path[i]);
        }

        return prefix;
    }

    /// <remarks>A NaN pressure would poison the whole outline (Direct2D then drops the shape silently).</remarks>
    private static float Radius(float pressure, float size, float thinning)
        => size / 2 * (1 - (thinning * (1 - (float.IsFinite(pressure) ? Math.Clamp(pressure, 0, 1) : 1))));

    /// <summary>Eases the radius to zero over the first and last <paramref name="taper"/> of length.</summary>
    private static float TaperFactor(float travelled, float total, float taper)
    {
        if (taper <= 0)
        {
            return 1;
        }

        var start = Math.Min(1, travelled / taper);
        var end = Math.Min(1, (total - travelled) / taper);
        static float Ease(float t) => t * (2 - t);
        return Math.Max(0.08f, Math.Min(Ease(start), Ease(end)));
    }

    private static Vector2 Direction(List<InkPoint> points, int index)
    {
        var from = points[Math.Max(0, index - 1)].Position;
        var to = points[Math.Min(points.Count - 1, index + 1)].Position;
        var delta = to - from;
        return delta == Vector2.Zero ? Vector2.UnitX : Vector2.Normalize(delta);
    }

    private static void AddCap(List<Vector2> outline, Vector2 centre, Vector2 forward, float radius)
    {
        var normal = new Vector2(-forward.Y, forward.X);
        for (var i = 1; i < CapSegments; i++)
        {
            var angle = MathF.PI * i / CapSegments;
            outline.Add(centre + (normal * MathF.Cos(angle) * radius) + (forward * MathF.Sin(angle) * radius));
        }
    }

    private static IReadOnlyList<Vector2> Circle(Vector2 centre, float radius)
    {
        var points = new Vector2[CapSegments * 2];
        for (var i = 0; i < points.Length; i++)
        {
            var angle = 2 * MathF.PI * i / points.Length;
            points[i] = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }

        return points;
    }

    private static List<InkPoint> Resample(IReadOnlyList<InkPoint> path, float spacing)
    {
        var kept = new List<InkPoint> { path[0] };
        for (var i = 1; i < path.Count; i++)
        {
            if (Vector2.Distance(kept[^1].Position, path[i].Position) >= spacing || i == path.Count - 1)
            {
                kept.Add(path[i]);
            }
        }

        return kept;
    }
}
