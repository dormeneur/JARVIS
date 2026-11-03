using System.Numerics;
using Jarvis.Contracts.Overlay;

namespace Jarvis.Core.Drawing;

/// <summary>
/// Pen paths that look drawn by hand: lines bow a little and overshoot their ends, circles don't
/// quite close, every stroke presses harder in the middle. Same idea as the rough.js library,
/// written for this app. All sizes are in physical pixels; <c>unit</c> is one DIP at the display scale.
/// </summary>
internal sealed class Sketch(Random random, float unit)
{
    /// <summary>Distance between generated points along a path.</summary>
    private float Spacing => 5 * unit;

    public IReadOnlyList<InkPoint> Line(Vector2 from, Vector2 to)
    {
        var length = Vector2.Distance(from, to);
        if (length < 1)
        {
            return [new InkPoint(from, 1)];
        }

        var direction = (to - from) / length;
        var normal = new Vector2(-direction.Y, direction.X);
        from += Jitter(Math.Min(2 * unit, length * 0.02f));
        to += (direction * Range(0, Math.Min(6 * unit, length * 0.04f))) + Jitter(Math.Min(2 * unit, length * 0.02f));

        var bow = Range(-1, 1) * Math.Min(length * 0.025f, 5 * unit);
        var control1 = Vector2.Lerp(from, to, 0.3f) + (normal * bow);
        var control2 = Vector2.Lerp(from, to, 0.7f) + (normal * bow * Range(0.4f, 1f));

        var count = Math.Max(8, (int)(length / Spacing));
        var points = new InkPoint[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var t = (float)i / count;
            points[i] = new InkPoint(Bezier(from, control1, control2, to, t), Pressure(t));
        }

        return points;
    }

    /// <summary>A loose ellipse around a rectangle that starts and ends a little apart, like a quick circle.</summary>
    public IReadOnlyList<InkPoint> Ring(Vector2 centre, Vector2 radius)
    {
        var start = Range(-2.6f, -2.0f);
        var sweep = (2 * MathF.PI) + Range(0.25f, 0.45f);
        var wobblePhase = Range(0, 2 * MathF.PI);
        var circumference = MathF.PI * (radius.X + radius.Y);
        var count = Math.Max(24, (int)(circumference * sweep / (2 * MathF.PI) / Spacing));

        var points = new InkPoint[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var t = (float)i / count;
            var angle = start + (sweep * t);
            var wobble = 1 + (0.035f * MathF.Sin((2 * angle) + wobblePhase)) + (0.05f * t);
            var position = centre + new Vector2(MathF.Cos(angle) * radius.X, MathF.Sin(angle) * radius.Y) * wobble;
            points[i] = new InkPoint(position, Pressure(t));
        }

        return points;
    }

    /// <summary>The two short strokes of an arrowhead at <paramref name="tip"/>, pointing along <paramref name="direction"/>.</summary>
    public (IReadOnlyList<InkPoint> Left, IReadOnlyList<InkPoint> Right) ArrowHead(Vector2 tip, Vector2 direction)
    {
        var back = -Vector2.Normalize(direction) * 15 * unit;
        var spread = 0.5f + Range(-0.06f, 0.06f);
        return (Line(tip + Rotate(back, spread), tip), Line(tip, tip + Rotate(back, -spread)));
    }

    /// <summary>
    /// Pen pressure along a stroke: light at the ends, firm through the middle. In single precision
    /// sin(π) is a hair below zero, so clamp before the square root or the last point becomes NaN.
    /// </summary>
    private static float Pressure(float t) => 0.5f + (0.5f * MathF.Sqrt(Math.Max(0, MathF.Sin(MathF.PI * t))));

    private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        var u = 1 - t;
        return (u * u * u * a) + (3 * u * u * t * b) + (3 * u * t * t * c) + (t * t * t * d);
    }

    private static Vector2 Rotate(Vector2 v, float angle)
        => new((v.X * MathF.Cos(angle)) - (v.Y * MathF.Sin(angle)), (v.X * MathF.Sin(angle)) + (v.Y * MathF.Cos(angle)));

    private Vector2 Jitter(float amount) => new(Range(-amount, amount), Range(-amount, amount));

    private float Range(float min, float max) => min + ((float)random.NextDouble() * (max - min));
}
