using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Jarvis.Contracts.Overlay;
using Jarvis.Core;
using Jarvis.Core.Cursor;
using Jarvis.Core.Drawing;
using Jarvis.Platform.Mac.Shell;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Platform.Mac.Overlay;

/// <summary>
/// The cursor companion, its bubble, ink, the highlighter trail and the control border, as
/// OverlayService does on Windows: the same Jarvis.Core state machines, run once per display
/// refresh (CADisplayLink, so 120 Hz on ProMotion) on the main thread, with the native overlay
/// panels only drawing what they're told. Commands from other threads are queued for the next frame.
/// </summary>
public sealed unsafe partial class MacOverlay : ICursorOverlay, IScreenDrawing, IHostedService
{
    private const float LabelPopSeconds = 0.18f;
    private const double TrailAlpha = 0.55;
    private const double BorderFadeSeconds = 0.3;
    private const double BorderFlowSeconds = 4;
    private static readonly TimeSpan InkFade = TimeSpan.FromSeconds(0.4);
    private static readonly TimeSpan InkLinger = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TrailFade = TimeSpan.FromSeconds(1);
    private static MacOverlay? _current;

    private readonly IOptionsMonitor<CursorOptions> _options;
    private readonly IOptionsMonitor<Jarvis.Core.Notch.NotchOptions> _notchOptions;
    private (double Width, double Height) _hardwareNotch;
    private readonly PauseSwitch _pause;
    private readonly TimeProvider _time;
    private readonly ILogger<MacOverlay> _logger;
    private readonly ConcurrentQueue<Action<TimeSpan>> _commands = new();
    private readonly CursorCompanion _cursor = new();
    private readonly Jarvis.Core.Gestures.Highlighter _highlighter = new();
    private readonly Jarvis.Core.Notch.NotchDwell _dwell = new();
    private readonly double[] _dots = new double[15];
    private readonly double[] _bars = new double[5];
    private IReadOnlyList<Screen> _screens = [];
    private ScreenHighlight? _highlight;
    private volatile bool _controlBorder;
    private volatile bool _fullscreen;
    private bool _highlighting;
    private TimeSpan? _trailFadeAt;
    private IReadOnlyList<(double X, double Y)> _trail = [];
    private ScreenDrawing? _drawing;
    private TimeSpan _drawingAt;
    private TimeSpan? _inkFadeAt;
    private bool _inkPainted;
    private double _border;
    private TimeSpan _lastFrame;
    private Timer? _fullscreenCheck;

    public MacOverlay(IOptionsMonitor<CursorOptions> options, IOptionsMonitor<Jarvis.Core.Notch.NotchOptions> notchOptions, PauseSwitch pause, TimeProvider time, ILogger<MacOverlay> logger)
    {
        _options = options;
        _notchOptions = notchOptions;
        _pause = pause;
        _time = time;
        _logger = logger;
    }

    public event Action? Clicked;

    /// <summary>Raised (on a worker thread) when the mouse has rested on the sleeping notch long enough to open it.</summary>
    public event Action? NotchDwelled;

    /// <summary>Whether resting on the notch may open it now (the notch is asleep); unset means never.</summary>
    public Func<bool>? DwellAllowed { get; set; }

    /// <summary>Lets screenshots see the overlay (Privacy → Show in recordings, or JARVIS_OVERLAY_CAPTURABLE=1).</summary>
    public bool ShowInRecordings { get; set; }

    public ScreenHighlight? TakeHighlight() => Interlocked.Exchange(ref _highlight, null);

    public void ShowControlBorder(bool on) => _controlBorder = on;

    public void SetThinking(bool thinking) => _commands.Enqueue(now => _cursor.SetThinking(thinking, now));

    public void SetListening(bool listening) => _commands.Enqueue(_ => _cursor.SetListening(listening));

    public void ShowLevel(double level) => _commands.Enqueue(_ => _cursor.ShowLevel(level));

    public void PointAt(int x, int y, string? label = null) => _commands.Enqueue(_ =>
    {
        _cursor.PointAt(x, y, label);
        Native.OverlayRaise();
    });

    public void ReturnToMouse() => _commands.Enqueue(_ => _cursor.ReturnToMouse());

    public void Show(ScreenDrawing drawing) => _commands.Enqueue(now =>
    {
        _drawing = drawing;
        _drawingAt = now;
        _inkFadeAt = null;
        Native.OverlayRaise();
    });

    public void Clear() => _commands.Enqueue(now =>
    {
        if (_drawing is not null)
        {
            _inkFadeAt ??= now;
        }
    });

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _current = this;
        // Fullscreen is a window-list query: once a second is plenty, and keeps it off the frame.
        _fullscreenCheck = new Timer(_ => _fullscreen = MacActivityWatch.Front() is { Fullscreen: true }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        MainThread.Post(() =>
        {
            _screens = Screen.All();
            Native.NotchHardware(out var notchWidth, out var notchHeight);
            _hardwareNotch = (notchWidth, notchHeight);
            Native.OverlayStart(&OnFrame, &OnClicked, &OnDisplaysChanged, ShowInRecordings ? 1 : 0);
            LogDisplays(_screens.Count);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _fullscreenCheck?.Dispose();
        _current = null;
        return Task.CompletedTask;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnFrame(double timestamp)
    {
        try
        {
            _current?.Frame();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // One bad frame (a malformed drawing, say) must not take the cursor down with it.
            _current?.LogFrameFailed(exception);
            if (_current is { } overlay)
            {
                overlay._drawing = null;
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnClicked()
    {
        if (_current is { } overlay && overlay._cursor.IsDetached)
        {
            overlay._commands.Enqueue(now => overlay._cursor.Clicked(now));
            // Off the main thread: starting the microphone must not stall a frame.
            ThreadPool.QueueUserWorkItem(_ => overlay.Clicked?.Invoke());
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnDisplaysChanged()
    {
        if (_current is { } overlay)
        {
            overlay._screens = Screen.All();
            Native.NotchHardware(out var notchWidth, out var notchHeight);
            overlay._hardwareNotch = (notchWidth, notchHeight);
            overlay._inkPainted = false;
            overlay.LogDisplays(overlay._screens.Count);
        }
    }

    private void Frame()
    {
        var now = _time.GetElapsedTime(0);
        var delta = _lastFrame == default ? 0 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        while (_commands.TryDequeue(out var command))
        {
            command(now);
        }

        var options = _options.CurrentValue;
        var rgb = CursorColor.Parse(options.Color);
        _cursor.ShakeToDetachEnabled = options.ShakeToDetach;
        _cursor.Follow = options.FollowSpring;
        Native.Mouse(out var mouseX, out var mouseY);

        // Desktop units are points, so Core's DIP distances apply as they are: scale 1.
        var pose = _cursor.Update(now, mouseX, mouseY, 1);
        var visible = options.Visible && !_pause.IsPaused && !(options.HideInFullscreen && _cursor.IsIdle && _fullscreen);

        // Resting on the sleeping notch fills a ring beside the cursor, then opens the notch's home.
        var main = _screens.Count > 0 ? _screens[0] : null;
        // With a camera housing the whole housing is the target, not the 60 pt sliver drawn on other Macs.
        var notchOptions = _notchOptions.CurrentValue;
        _dwell.Delay = TimeSpan.FromMilliseconds(Math.Clamp(notchOptions.HoverDelayMs, 0, 2000));
        var onSliver = main is not null && (_hardwareNotch.Width > 0
            ? Jarvis.Core.Notch.NotchDwell.OnHardwareNotch(mouseX, mouseY, main.Left, main.Top, main.Width, _hardwareNotch.Width, _hardwareNotch.Height)
            : Jarvis.Core.Notch.NotchDwell.OnSliver(mouseX, mouseY, main.Left, main.Top, main.Width, 1));
        // Not tied to the floating cursor: hidden (or stepped aside for fullscreen), the notch still opens.
        var mayOpen = notchOptions.OpenOnHover && !_cursor.IsListening && (Native.MouseButtons() & 1) == 0 && DwellAllowed?.Invoke() == true;
        var (ring, opened) = _dwell.Update(now, onSliver, mayOpen);
        pose = pose with { Dwell = ring };
        if (opened && NotchDwelled is { } open)
        {
            _ = Task.Run(open);
        }

        DrawCursor(pose, visible, rgb);
        Native.Bubble(visible ? pose.Bubble : null, pose.X, pose.Y, rgb);
        Native.Catcher(visible && _cursor.IsDetached ? 1 : 0, pose.X, pose.Y);
        UpdateTrail(options, visible, mouseX, mouseY, now, rgb);
        DrawInk(now);

        // The control border fades in and out over 0.3 s and flows once round every 4 s.
        var target = _controlBorder ? 1 : 0;
        var step = delta / BorderFadeSeconds;
        _border = target > _border ? Math.Min(target, _border + step) : Math.Max(target, _border - step);
        if (_border > 0 || target > 0)
        {
            Native.Border(_border, now.TotalSeconds / BorderFlowSeconds % 1);
        }
    }

    private void DrawCursor(CursorPose pose, bool visible, uint rgb)
    {
        fixed (double* dots = _dots)
        fixed (double* bars = _bars)
        {
            var dotsArgument = (double*)null;
            var barsArgument = (double*)null;
            if (pose.ListeningLevel is { } level)
            {
                for (var bar = 0; bar < ListeningBars.Count; bar++)
                {
                    _bars[bar] = ListeningBars.Height(bar, level);
                }

                barsArgument = bars;
            }
            else if (pose.ThinkingSeconds is { } seconds)
            {
                for (var dot = 0; dot < 3; dot++)
                {
                    var p = ThinkingDots.Pose(dot, seconds);
                    (_dots[dot * 5], _dots[(dot * 5) + 1], _dots[(dot * 5) + 2], _dots[(dot * 5) + 3], _dots[(dot * 5) + 4]) = (p.X, p.Y, p.RadiusAlong, p.AcrossScale, p.AngleRadians);
                }

                dotsArgument = dots;
            }

            Native.Cursor(visible ? 1 : 0, pose.X, pose.Y, pose.RotationDegrees, pose.Scale * Math.Clamp(_options.CurrentValue.Size, 0.5, 2), rgb, dotsArgument, barsArgument, pose.Dwell ?? -1);
        }
    }

    /// <summary>Talk keys held: the mouse leaves a highlighter trail the turn reads as "this".</summary>
    private void UpdateTrail(CursorOptions options, bool visible, double x, double y, TimeSpan now, uint rgb)
    {
        if (_cursor.IsListening && options.Highlighter && visible)
        {
            if (!_highlighting)
            {
                _highlighting = true;
                _highlight = null;
                _trailFadeAt = null;
                _highlighter.Start(x, y, 1, now);
            }
            else
            {
                _highlighter.Add(x, y, now);
            }

            if (_highlighter.IsDrawing)
            {
                _trail = _highlighter.Path.ToList();
                SendTrail(rgb, TrailAlpha);
            }

            return;
        }

        if (_highlighting)
        {
            _highlighting = false;
            _highlight = _highlighter.Finish();
            _trailFadeAt = now;
        }

        if (_trailFadeAt is { } fadeAt && _trail.Count > 0)
        {
            var opacity = 1 - ((now - fadeAt) / TrailFade);
            if (opacity <= 0)
            {
                _trail = [];
                _trailFadeAt = null;
            }

            SendTrail(rgb, TrailAlpha * Math.Max(0, opacity));
        }
    }

    private void SendTrail(uint rgb, double alpha)
    {
        var points = new double[_trail.Count * 2];
        for (var i = 0; i < _trail.Count; i++)
        {
            (points[i * 2], points[(i * 2) + 1]) = _trail[i];
        }

        fixed (double* pointer = points)
        {
            Native.Highlight(pointer, _trail.Count, rgb, alpha);
        }
    }

    /// <summary>
    /// The pen reveal, labels popping in, turning 3D figures and the fade, all from Jarvis.Core
    /// (StrokeOutline, Figure3D); a finished still drawing isn't re-sent.
    /// </summary>
    private void DrawInk(TimeSpan now)
    {
        if (_drawing is not { } drawing)
        {
            if (_inkPainted)
            {
                Native.InkCommit(0, 0);
                _inkPainted = false;
            }

            return;
        }

        var elapsed = now - _drawingAt;
        if (_inkFadeAt is null && elapsed > drawing.Duration + InkLinger)
        {
            _inkFadeAt = now;
        }

        var opacity = _inkFadeAt is { } fadeAt ? 1 - (float)((now - fadeAt) / InkFade) : 1f;
        if (opacity <= 0)
        {
            _drawing = null;
            return;
        }

        var animating = elapsed.TotalSeconds <= drawing.Duration.TotalSeconds + LabelPopSeconds || _inkFadeAt is not null || drawing.Figures.Count > 0;
        if (!animating && _inkPainted)
        {
            return;
        }

        foreach (var stroke in drawing.Strokes)
        {
            var progress = (float)Math.Clamp((elapsed - stroke.Start) / stroke.Duration, 0, 1);
            if (progress <= 0)
            {
                continue;
            }

            var outline = StrokeOutline.Build(StrokeOutline.Prefix(stroke.Points, progress), stroke.Width, taper: stroke.Width * 2);
            Shape(outline, drawing.Rgb, opacity, kind: 0);
        }

        var labels = drawing.Labels.ToList();
        foreach (var figure in drawing.Figures)
        {
            var frame = Figure3D.Project(figure, elapsed, drawing.Scale);
            foreach (var face in frame.Faces)
            {
                Shape(face.Points, face.Rgb, opacity, kind: 1);
            }

            labels.AddRange(frame.Labels);
        }

        foreach (var label in labels)
        {
            var since = elapsed - label.Start;
            if (since >= TimeSpan.Zero)
            {
                Native.InkLabel(label.Text, label.Anchor.X, label.Anchor.Y, label.Style == InkLabelStyle.Badge ? 1 : 0, label.GrowsLeft ? 1 : 0, PopScale(since), drawing.Rgb, opacity);
            }
        }

        var anchor = drawing.Strokes.Count > 0 ? drawing.Strokes[0].Points[0].Position
            : drawing.Labels.Count > 0 ? drawing.Labels[0].Anchor
            : drawing.Figures.Count > 0 ? drawing.Figures[0].Centre
            : Vector2.Zero;
        Native.InkCommit(anchor.X, anchor.Y);
        _inkPainted = true;
    }

    private static void Shape(IReadOnlyList<Vector2> points, uint rgb, float alpha, int kind)
    {
        if (points.Count < 3)
        {
            return;
        }

        var flat = new float[points.Count * 2];
        for (var i = 0; i < points.Count; i++)
        {
            (flat[i * 2], flat[(i * 2) + 1]) = (points[i].X, points[i].Y);
        }

        fixed (float* pointer = flat)
        {
            Native.InkShape(pointer, points.Count, rgb, alpha, kind);
        }
    }

    /// <summary>Labels pop from 60 % with a little overshoot (easeOutBack), as on Windows.</summary>
    private static float PopScale(TimeSpan since)
    {
        var t = (float)Math.Clamp(since.TotalSeconds / LabelPopSeconds, 0, 1);
        const float Overshoot = 1.7f;
        var u = t - 1;
        return 0.6f + (0.4f * (1 + ((Overshoot + 1) * u * u * u) + (Overshoot * u * u)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Overlay covering {Count} display(s)")]
    private partial void LogDisplays(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "An overlay frame failed; the drawing was dropped")]
    private partial void LogFrameFailed(Exception exception);
}
