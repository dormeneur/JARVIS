using System.Numerics;
using Jarvis.Core.Motion;
using Jarvis.Core.Notch;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace Jarvis.App.Notch;

/// <summary>
/// The notch's look, built from composition visuals. Shape, glow and sweep are all driven by
/// expressions over one property set whose values are spring-animated on the compositor thread,
/// so the motion stays smooth no matter what the UI thread is doing.
/// </summary>
internal sealed class NotchSurface
{
    private const float Shoulder = (float)NotchLayout.ShoulderRadius;
    private const float GlowOpacity = 0.85f;
    private const float SweepBandWidth = 30;
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>Corner-glow blobs: centre as fractions from the bottom-right, radius factor, intensity.</summary>
    private static readonly (float FromRight, float Down, float Radius, float Intensity)[] Blobs =
    [
        (0.05f, 1.00f, 1.05f, 1.0f),
        (0.22f, 0.95f, 0.85f, 1.0f),
        (0.42f, 0.92f, 0.65f, 0.9f),
    ];

    private static readonly float[] BlobStops = [0f, 0.35f, 0.65f, 0.85f, 1f];
    private static readonly float[] BlobFactors = [1f, 0.95f, 0.7f, 0.3f, 0f];
    private static readonly float[] MaskStops = [0f, 0.42f, 0.55f, 0.68f, 0.8f, 0.9f, 0.97f, 1f];
    private static readonly float[] MaskAlphas = [1f, 1f, 0.88f, 0.65f, 0.38f, 0.18f, 0.05f, 0f];

    private readonly Compositor _compositor;
    private readonly CompositionPropertySet _props;
    private readonly ContainerVisual _glow;
    private readonly ContainerVisual _sweep;
    private readonly SpriteVisual _sweepBand;
    private readonly CompositionColorGradientStop[][] _blobStops;
    private bool _sweepRunning;
    private bool _glowRunning;

    public NotchSurface(UIElement host, float envelopeHeight)
    {
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _props = _compositor.CreatePropertySet();
        foreach (var name in new[] { "Width", "Height", "Radius", "Sliver", "Concavity", "Gap", "Orbit", "Pulse", "GlowH" })
        {
            _props.InsertScalar(name, 0);
        }

        _props.InsertScalar("EnvW", (float)NotchLayout.EnvelopeWidth);

        var root = _compositor.CreateContainerVisual();
        root.Size = new Vector2((float)NotchLayout.EnvelopeWidth, envelopeHeight);
        root.Children.InsertAtTop(CreateBody(root.Size));
        root.Children.InsertAtTop(CreateShoulder(left: true));
        root.Children.InsertAtTop(CreateShoulder(left: false));

        var inside = _compositor.CreateContainerVisual();
        inside.Size = root.Size;
        inside.Clip = _compositor.CreateGeometricClip(CreateBodyGeometry());
        root.Children.InsertAtTop(inside);

        _blobStops = new CompositionColorGradientStop[Blobs.Length][];
        _glow = CreatePanelGroup();
        for (var i = 0; i < Blobs.Length; i++)
        {
            _glow.Children.InsertAtTop(CreateBlob(i));
        }

        (_sweep, _sweepBand) = CreateSweep();
        inside.Children.InsertAtTop(_glow);
        inside.Children.InsertAtTop(_sweep);

        ElementCompositionPreview.SetElementChildVisual(host, root);
    }

    /// <summary>Springs (or snaps) the shape to <paramref name="frame"/> and fades the lighting in or out.</summary>
    /// <param name="glowReach">How tall the glow may reach (DIPs); null for the panel's own height (the spec).</param>
    /// <param name="glowStrength">0..1 of the glow's usual opacity.</param>
    public void Apply(NotchFrame frame, IReadOnlyList<uint>? glowColors, bool glowMoves, bool working, double? glowReach = null, double glowStrength = 1)
    {
        Animate("GlowH", (float)Math.Min(frame.Height, glowReach ?? frame.Height), frame.Spring);
        var sliver = frame.IsSliver ? 1f : 0f;
        Animate("Width", (float)frame.Width, frame.Spring);
        Animate("Height", (float)frame.Height, frame.Spring);
        Animate("Radius", (float)frame.BottomRadius, frame.Spring);
        Animate("Sliver", sliver, frame.Spring);
        Animate("Concavity", 1 - sliver, frame.Spring);
        Animate("Gap", (float)(frame.IsSliver ? NotchLayout.SliverTopGap : 0), frame.Spring);

        if (glowColors is not null)
        {
            SetGlowColors(glowColors);
        }

        Fade(_glow, glowColors is null ? 0 : GlowOpacity * (float)glowStrength);
        SetGlowRunning(glowColors is not null && glowMoves);
        Fade(_sweep, working ? 1 : 0);
        SetSweepRunning(working);
    }

    private void Animate(string property, float value, Spring? spring)
    {
        if (spring is not { } motion)
        {
            _props.StopAnimation(property);
            _props.InsertScalar(property, value);
            return;
        }

        var animation = _compositor.CreateSpringScalarAnimation();
        animation.DampingRatio = (float)motion.DampingFraction;
        animation.Period = motion.CompositionPeriod;
        animation.FinalValue = value;
        animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
        _props.StartAnimation(property, animation);
    }

    private const string BodyOffset = "Vector2((p.EnvW - p.Width) / 2 + 10 * p.Concavity, p.Gap - p.Radius * p.Concavity)";
    private const string BodySize = "Vector2(p.Width - 20 * p.Concavity, p.Height + p.Radius * p.Concavity)";
    private const string BodyCorner = "(0.854 + 0.146 * p.Sliver) * Min(p.Radius, Min(p.Width - 20 * p.Concavity, p.Height) / 2)";

    /// <summary>
    /// A rounded rectangle. When attached to the top edge (concavity 1) it is pushed up by its own
    /// radius so the top corners vanish above the window, leaving a flat top flush with the screen.
    /// </summary>
    private CompositionRoundedRectangleGeometry CreateBodyGeometry()
    {
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        Bind(geometry, "Offset", BodyOffset);
        Bind(geometry, "Size", BodySize);
        Bind(geometry, "CornerRadius", $"Vector2({BodyCorner}, {BodyCorner})");
        return geometry;
    }

    private ShapeVisual CreateBody(Vector2 size)
    {
        var shape = _compositor.CreateSpriteShape(CreateBodyGeometry());
        shape.FillBrush = _compositor.CreateColorBrush(Colors.Black);
        // The floating sliver gets a faint outline so it reads against dark wallpapers.
        shape.StrokeBrush = _compositor.CreateColorBrush(Color.FromArgb(61, 255, 255, 255));
        Bind(shape, "StrokeThickness", "p.Sliver");

        var visual = _compositor.CreateShapeVisual();
        visual.Size = size;
        visual.Shapes.Add(shape);
        return visual;
    }

    /// <summary>
    /// The concave fillet where the panel meets the screen edge, like a MacBook notch. The filled
    /// region is the square between the edge and the body minus a quarter circle.
    /// </summary>
    private ShapeVisual CreateShoulder(bool left)
    {
        using var builder = new CanvasPathBuilder(null);
        if (left)
        {
            builder.BeginFigure(0, 0);
            builder.AddLine(Shoulder, 0);
            builder.AddLine(Shoulder, Shoulder);
            builder.AddArc(new Vector2(0, 0), Shoulder, Shoulder, 0, CanvasSweepDirection.CounterClockwise, CanvasArcSize.Small);
        }
        else
        {
            builder.BeginFigure(Shoulder, 0);
            builder.AddLine(0, 0);
            builder.AddLine(0, Shoulder);
            builder.AddArc(new Vector2(Shoulder, 0), Shoulder, Shoulder, 0, CanvasSweepDirection.Clockwise, CanvasArcSize.Small);
        }

        builder.EndFigure(CanvasFigureLoop.Closed);
        var path = _compositor.CreatePathGeometry(new CompositionPath(CanvasGeometry.CreatePath(builder)));
        var shape = _compositor.CreateSpriteShape(path);
        shape.FillBrush = _compositor.CreateColorBrush(Colors.Black);

        var visual = _compositor.CreateShapeVisual();
        visual.Size = new Vector2(Shoulder, Shoulder);
        visual.Shapes.Add(shape);
        Bind(visual, "Offset", left
            ? "Vector3((p.EnvW - p.Width) / 2, 0, 0)"
            : "Vector3((p.EnvW + p.Width) / 2 - 10, 0, 0)");
        Bind(visual, "Opacity", "p.Concavity");
        return visual;
    }

    /// <summary>A container that tracks the panel's rectangle, for content drawn inside the shape.</summary>
    private ContainerVisual CreatePanelGroup()
    {
        var group = _compositor.CreateContainerVisual();
        group.Opacity = 0;
        Bind(group, "Offset", "Vector3((p.EnvW - p.Width) / 2, p.Gap, 0)");
        Bind(group, "Size", "Vector2(p.Width, p.Height)");
        return group;
    }

    /// <summary>One soft light in the bottom-right corner, slowly orbiting, faded out toward the panel's interior.</summary>
    private SpriteVisual CreateBlob(int index)
    {
        var (fromRight, down, radius, intensity) = Blobs[index];
        var phase = index * 2.1f;

        var blob = _compositor.CreateRadialGradientBrush();
        blob.MappingMode = CompositionMappingMode.Absolute;
        _blobStops[index] = new CompositionColorGradientStop[BlobStops.Length];
        for (var stop = 0; stop < BlobStops.Length; stop++)
        {
            _blobStops[index][stop] = _compositor.CreateColorGradientStop(BlobStops[stop], Colors.Transparent);
            blob.ColorStops.Add(_blobStops[index][stop]);
        }

        Bind(blob, "EllipseCenter", Invariant($"Vector2(p.Width * {1 - fromRight} + 6 * Cos(p.Orbit + {phase}), p.Height - p.GlowH * {1 - down} + 6 * Sin(p.Orbit + {phase}))"));
        Bind(blob, "EllipseRadius", Invariant($"Vector2(Max(p.GlowH, 40) * {radius}, Max(p.GlowH, 40) * {radius})"));

        var mask = _compositor.CreateRadialGradientBrush();
        mask.MappingMode = CompositionMappingMode.Absolute;
        for (var stop = 0; stop < MaskStops.Length; stop++)
        {
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(MaskStops[stop], Color.FromArgb((byte)(MaskAlphas[stop] * 255), 255, 255, 255)));
        }

        Bind(mask, "EllipseCenter", "Vector2(p.Width, p.Height)");
        Bind(mask, "EllipseRadius", "Vector2(p.GlowH * 1.6, p.GlowH * 1.6)");

        var masked = _compositor.CreateMaskBrush();
        masked.Source = blob;
        masked.Mask = mask;

        var sprite = _compositor.CreateSpriteVisual();
        sprite.Brush = masked;
        sprite.RelativeSizeAdjustment = Vector2.One;
        Bind(sprite, "Opacity", Invariant($"{intensity} * (0.9 + 0.1 * Sin(p.Pulse))"));
        return sprite;
    }

    /// <summary>Working indicator: a green tint with a mint light crossing it.</summary>
    private (ContainerVisual Group, SpriteVisual Band) CreateSweep()
    {
        var group = CreatePanelGroup();

        var tint = _compositor.CreateSpriteVisual();
        tint.Brush = _compositor.CreateColorBrush(WithAlpha(NotchAccent.SweepTint, 0.26));
        tint.RelativeSizeAdjustment = Vector2.One;
        group.Children.InsertAtTop(tint);

        var light = _compositor.CreateLinearGradientBrush();
        light.StartPoint = new Vector2(0, 0.5f);
        light.EndPoint = new Vector2(1, 0.5f);
        light.ColorStops.Add(_compositor.CreateColorGradientStop(0, WithAlpha(NotchAccent.SweepLight, 0)));
        light.ColorStops.Add(_compositor.CreateColorGradientStop(0.5f, WithAlpha(NotchAccent.SweepLight, 0.75)));
        light.ColorStops.Add(_compositor.CreateColorGradientStop(1, WithAlpha(NotchAccent.SweepLight, 0)));

        var band = _compositor.CreateSpriteVisual();
        band.Brush = light;
        band.Size = new Vector2(SweepBandWidth, 0);
        band.RelativeSizeAdjustment = new Vector2(0, 1);
        band.Offset = new Vector3(-SweepBandWidth, 0, 0);
        group.Children.InsertAtTop(band);
        return (group, band);
    }

    private void SetSweepRunning(bool running)
    {
        if (running == _sweepRunning)
        {
            return;
        }

        _sweepRunning = running;
        if (!running)
        {
            _sweepBand.StopAnimation("Offset.X");
            return;
        }

        // Cross in 1.1 s, rest 0.5 s, repeat; first pass after 0.3 s.
        var cross = _compositor.CreateScalarKeyFrameAnimation();
        var linear = _compositor.CreateLinearEasingFunction();
        cross.InsertKeyFrame(0, -SweepBandWidth, linear);
        cross.InsertExpressionKeyFrame(1.1f / 1.6f, "p.Width", linear);
        cross.InsertExpressionKeyFrame(1, "p.Width", linear);
        cross.SetReferenceParameter("p", _props);
        cross.Duration = TimeSpan.FromSeconds(1.6);
        cross.DelayTime = TimeSpan.FromSeconds(0.3);
        cross.IterationBehavior = AnimationIterationBehavior.Forever;
        _sweepBand.StartAnimation("Offset.X", cross);
    }

    /// <summary>
    /// WinUI's compositor runs in our process, so a forever-animation costs CPU every frame even
    /// when invisible. The orbit and pulse only run while a moving glow is lit.
    /// </summary>
    private void SetGlowRunning(bool running)
    {
        if (running == _glowRunning)
        {
            return;
        }

        _glowRunning = running;
        // ponytail: gradients are re-rasterised every frame while lit (measured ~5-25 % of a core on
        // a busy machine). Fine for seconds-long live states; if it ever matters, render the blobs
        // once into a cached surface and orbit the visual instead of the brush centre.
        if (running)
        {
            StartLoop("Orbit", TimeSpan.FromSeconds(12));
            StartLoop("Pulse", TimeSpan.FromSeconds(6));
        }
        else
        {
            _props.StopAnimation("Orbit");
            _props.StopAnimation("Pulse");
        }
    }

    private void SetGlowColors(IReadOnlyList<uint> colors)
    {
        for (var blob = 0; blob < _blobStops.Length; blob++)
        {
            var rgb = colors[Math.Min(blob, colors.Count - 1)];
            for (var stop = 0; stop < BlobFactors.Length; stop++)
            {
                _blobStops[blob][stop].Color = WithAlpha(rgb, BlobFactors[stop]);
            }
        }
    }

    private void Fade(Visual visual, float to)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to);
        fade.Duration = FadeDuration;
        visual.StartAnimation("Opacity", fade);
    }

    private void StartLoop(string property, TimeSpan period)
    {
        var loop = _compositor.CreateScalarKeyFrameAnimation();
        var linear = _compositor.CreateLinearEasingFunction();
        loop.InsertKeyFrame(0, 0, linear);
        loop.InsertKeyFrame(1, 2 * MathF.PI, linear);
        loop.Duration = period;
        loop.IterationBehavior = AnimationIterationBehavior.Forever;
        _props.StartAnimation(property, loop);
    }

    private void Bind(CompositionObject target, string property, string expression)
    {
        var animation = _compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _props);
        target.StartAnimation(property, animation);
    }

    private static Color WithAlpha(uint rgb, double alpha)
        => Color.FromArgb((byte)Math.Round(alpha * 255), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
