using System.Numerics;
using Jarvis.Contracts.Overlay;

namespace Jarvis.Core.Drawing;

/// <summary>
/// Turns an <see cref="InkFigure"/> into flat-shaded polygons for one moment: builds each solid's
/// faces, grows the figure in, turns it slowly about its vertical axis under a slight downward
/// tilt, lights it from the upper left, drops faces turned away, and sorts the rest back to front.
/// Pure maths, so the renderer only fills polygons.
/// </summary>
public static class Figure3D
{
    /// <summary>One full turn; slow enough to read, quick enough to show every side.</summary>
    public static readonly TimeSpan Turn = TimeSpan.FromSeconds(14);

    private const int Segments = 24;
    private const int SphereRings = 12;
    private const float Tilt = 0.4f; // radians, looking a little down onto the figure
    private const float StartYaw = 0.6f; // so a box starts corner-on, not flat to the viewer
    private const float CameraDistance = 4.5f;
    private const float Ambient = 0.42f;
    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(-0.35f, 0.6f, 0.72f));

    /// <summary>A polygon on the desktop, already shaded, in painting order.</summary>
    public sealed record Face(Vector2[] Points, uint Rgb);

    public sealed record Frame(IReadOnlyList<Face> Faces, IReadOnlyList<InkLabel> Labels);

    /// <param name="elapsed">Time since the drawing was shown.</param>
    /// <param name="scale">Display scale, for label spacing.</param>
    public static Frame Project(InkFigure figure, TimeSpan elapsed, float scale)
    {
        ArgumentNullException.ThrowIfNull(figure);
        var since = elapsed - figure.Start;
        if (since < TimeSpan.Zero)
        {
            return new Frame([], []);
        }

        var t = (float)Math.Clamp(since / ScreenDrawing.FigureGrow, 0, 1);
        var grow = 1 - ((1 - t) * (1 - t) * (1 - t)); // ease-out cubic
        var yaw = StartYaw + (float)(2 * Math.PI * (since / Turn));
        var view = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(Tilt);
        var pixels = figure.Size / 2 / 1.3f; // figure units to pixels, with room for perspective

        Vector3 ToView(Vector3 point) => Vector3.Transform(point * grow, view);
        Vector2 ToScreen(Vector3 point)
        {
            var perspective = CameraDistance / (CameraDistance - point.Z);
            return figure.Centre + (new Vector2(point.X, -point.Y) * perspective * pixels);
        }

        var faces = new List<(Face Face, float Depth)>();
        foreach (var part in figure.Parts)
        {
            foreach (var polygon in Mesh(part))
            {
                var points = Array.ConvertAll(polygon, ToView);
                var centroid = Centroid(points);
                var normal = Normal(points, centroid - ToView(Middle(part)));
                if (Vector3.Dot(normal, new Vector3(0, 0, CameraDistance) - centroid) <= 0)
                {
                    continue; // facing away
                }

                var light = Ambient + ((1 - Ambient) * Math.Max(0, Vector3.Dot(normal, Light)));
                faces.Add((new Face(Array.ConvertAll(points, ToScreen), Shade(part.Rgb, light)), centroid.Z));
            }
        }

        var labels = new List<InkLabel>();
        var labelsAt = figure.Start + ScreenDrawing.FigureGrow;
        foreach (var part in figure.Parts.Where(part => !string.IsNullOrWhiteSpace(part.Label)))
        {
            var top = Middle(part) + new Vector3(0, Height(part) / 2, 0);
            labels.Add(new InkLabel(part.Label!.Trim(), ToScreen(ToView(top)) + new Vector2(6 * scale, -14 * scale), InkLabelStyle.Pill, labelsAt));
        }

        if (!string.IsNullOrWhiteSpace(figure.Title))
        {
            var title = figure.Title.Trim();
            var halfWidth = ((title.Length * 7.5f) + 18) * scale / 2; // estimated, as in SceneBuilder
            labels.Add(new InkLabel(title, figure.Centre + new Vector2(-halfWidth, (figure.Size / 2) + (16 * scale)), InkLabelStyle.Pill, labelsAt));
        }

        return new Frame([.. faces.OrderBy(face => face.Depth).Select(face => face.Face)], labels);
    }

    /// <summary>The solid's faces in figure space, each a flat polygon.</summary>
    internal static IEnumerable<Vector3[]> Mesh(FigurePart part) => part.Shape switch
    {
        FigureShape.Box => Box(Vector3.Min(part.From, part.To), Vector3.Max(part.From, part.To)),
        FigureShape.Sphere => Sphere(part.From, part.Radius),
        FigureShape.Cone => Tube(part.From, part.To, part.Radius, 0),
        _ => Tube(part.From, part.To, part.Radius, part.Radius),
    };

    private static IEnumerable<Vector3[]> Box(Vector3 min, Vector3 max)
    {
        Vector3 Corner(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        yield return [Corner(0, 0, 0), Corner(1, 0, 0), Corner(1, 1, 0), Corner(0, 1, 0)]; // back
        yield return [Corner(0, 0, 1), Corner(1, 0, 1), Corner(1, 1, 1), Corner(0, 1, 1)]; // front
        yield return [Corner(0, 0, 0), Corner(0, 0, 1), Corner(0, 1, 1), Corner(0, 1, 0)]; // left
        yield return [Corner(1, 0, 0), Corner(1, 0, 1), Corner(1, 1, 1), Corner(1, 1, 0)]; // right
        yield return [Corner(0, 0, 0), Corner(1, 0, 0), Corner(1, 0, 1), Corner(0, 0, 1)]; // bottom
        yield return [Corner(0, 1, 0), Corner(1, 1, 0), Corner(1, 1, 1), Corner(0, 1, 1)]; // top
    }

    private static IEnumerable<Vector3[]> Sphere(Vector3 centre, float radius)
    {
        Vector3 At(int ring, int segment)
        {
            var polar = Math.PI * ring / SphereRings;
            var around = 2 * Math.PI * segment / Segments;
            return centre + (radius * new Vector3((float)(Math.Sin(polar) * Math.Cos(around)), (float)Math.Cos(polar), (float)(Math.Sin(polar) * Math.Sin(around))));
        }

        for (var ring = 0; ring < SphereRings; ring++)
        {
            for (var segment = 0; segment < Segments; segment++)
            {
                // The poles' quads collapse to triangles; a repeated point is harmless.
                yield return [At(ring, segment), At(ring, segment + 1), At(ring + 1, segment + 1), At(ring + 1, segment)];
            }
        }
    }

    /// <summary>A cylinder (equal radii) or cone (top radius 0) from the base centre to the top.</summary>
    private static IEnumerable<Vector3[]> Tube(Vector3 bottom, Vector3 top, float radius, float topRadius)
    {
        var axis = top - bottom;
        if (axis.LengthSquared() < 1e-6f)
        {
            yield break;
        }

        var direction = Vector3.Normalize(axis);
        var side = Vector3.Normalize(Vector3.Cross(direction, Math.Abs(direction.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var other = Vector3.Cross(direction, side);
        Vector3 Rim(Vector3 centre, float r, int segment)
        {
            var angle = 2 * Math.PI * segment / Segments;
            return centre + (r * (((float)Math.Cos(angle) * side) + ((float)Math.Sin(angle) * other)));
        }

        var bottomRim = Enumerable.Range(0, Segments).Select(i => Rim(bottom, radius, i)).ToArray();
        var topRim = Enumerable.Range(0, Segments).Select(i => Rim(top, topRadius, i)).ToArray();
        for (var i = 0; i < Segments; i++)
        {
            var next = (i + 1) % Segments;
            yield return topRadius > 0 ? [bottomRim[i], bottomRim[next], topRim[next], topRim[i]] : [bottomRim[i], bottomRim[next], top];
        }

        yield return bottomRim;
        if (topRadius > 0)
        {
            yield return topRim;
        }
    }

    private static Vector3 Middle(FigurePart part) => (part.From + part.To) / 2;

    private static float Height(FigurePart part) => part.Shape switch
    {
        FigureShape.Box => Math.Abs(part.To.Y - part.From.Y),
        FigureShape.Sphere => part.Radius * 2,
        _ => Math.Abs(part.To.Y - part.From.Y) + (part.Radius * 0.5f),
    };

    private static Vector3 Centroid(Vector3[] points)
    {
        var sum = Vector3.Zero;
        foreach (var point in points)
        {
            sum += point;
        }

        return sum / points.Length;
    }

    /// <summary>The polygon's unit normal, turned to point away from the solid's middle (every solid is convex).</summary>
    private static Vector3 Normal(Vector3[] points, Vector3 outward)
    {
        // Newell's method: robust for the degenerate quads at a sphere's poles.
        var normal = Vector3.Zero;
        for (var i = 0; i < points.Length; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Length]);
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }

        if (normal.LengthSquared() < 1e-12f)
        {
            return Vector3.Zero;
        }

        normal = Vector3.Normalize(normal);
        return Vector3.Dot(normal, outward) < 0 ? -normal : normal;
    }

    private static uint Shade(uint rgb, float light)
    {
        uint Channel(int shift) => (uint)Math.Clamp(((rgb >> shift) & 0xFF) * light, 0, 255) << shift;
        return Channel(16) | Channel(8) | Channel(0);
    }
}
