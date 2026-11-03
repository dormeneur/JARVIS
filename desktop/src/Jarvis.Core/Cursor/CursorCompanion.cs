using Jarvis.Core.Gestures;
using Jarvis.Core.Motion;

namespace Jarvis.Core.Cursor;

/// <summary>
/// Decides where the companion cursor is and how it looks, one frame at a time. Pure logic: the
/// caller supplies time and the mouse position; a renderer draws the returned <see cref="CursorPose"/>.
/// Positions are physical pixels on the virtual desktop; distances in this class are DIPs scaled by
/// the display scale of the monitor under the mouse.
/// </summary>
public sealed class CursorCompanion
{
    private const double FollowOffsetX = 35;
    private const double FollowOffsetY = 25;
    private const double TargetOffsetX = 8;
    private const double TargetOffsetY = 12;
    private const double RestingRotation = -35;

    /// <summary>Said on arrival when the brain gave no label. Ours; taken in turn, not at random.</summary>
    private static readonly string[] Phrases = ["over here", "this one", "look here", "that's it", "spotted it"];

    /// <summary>Time for the listening meter to fall to 1/e of a peak.</summary>
    private const double LevelFallSeconds = 0.15;
    private const double ReturnDistance = 100;
    private const double MaximumFrameSeconds = 0.1;
    private static readonly TimeSpan DetachDuration = TimeSpan.FromSeconds(3);

    /// <summary>How long a clicked, left-behind cursor stays put (DESIGN-FACTS.md §3).</summary>
    private static readonly TimeSpan ClickedDetachDuration = TimeSpan.FromSeconds(5);

    private readonly ShakeDetector _shakeDetector;
    private SpringPoint _position;
    private double _rotation = RestingRotation;
    private double _level;
    private string? _bubble;
    private int _phrase;
    private double _levelTarget;
    private double _rotationVelocity;
    private CursorPhase _phase = CursorPhase.Following;
    private TimeSpan? _lastUpdate;
    private TimeSpan _phaseStartedAt;
    private bool _clicked;
    private FlightPath? _flight;
    private (double X, double Y) _pendingTarget;
    private bool _flightRequested;
    private (double X, double Y) _mouseAtLanding;
    private TimeSpan? _thinkingSince;
    private bool _initialised;

    public CursorCompanion(ShakeDetector? shakeDetector = null)
    {
        _shakeDetector = shakeDetector ?? new ShakeDetector();
    }

    public bool ShakeToDetachEnabled { get; set; } = true;

    public CursorPhase Phase => _phase;

    public bool IsDetached => _phase == CursorPhase.Detached;

    public bool IsThinking => _thinkingSince is not null;

    public bool IsListening { get; private set; }

    /// <summary>Just trailing the mouse: nothing is happening that the user asked for.</summary>
    public bool IsIdle => _phase == CursorPhase.Following && !IsThinking && !IsListening;

    /// <summary>Starts or stops the thinking dots; takes effect on the next <see cref="Update"/>.</summary>
    public void SetThinking(bool thinking, TimeSpan now)
        => _thinkingSince = thinking ? _thinkingSince ?? now : null;

    /// <summary>Starts or stops the listening meter; takes effect on the next <see cref="Update"/>.</summary>
    public void SetListening(bool listening)
    {
        IsListening = listening;
        _levelTarget = 0;
        _level = 0;
    }

    /// <summary>The microphone's latest 0–1 level; ignored unless listening.</summary>
    public void ShowLevel(double level)
    {
        if (IsListening)
        {
            _levelTarget = Math.Clamp(level, 0, 1);
        }
    }

    /// <summary>The left-behind cursor was clicked: it stays a little longer, and while listening.</summary>
    public void Clicked(TimeSpan now)
    {
        if (_phase == CursorPhase.Detached)
        {
            _clicked = true;
            _phaseStartedAt = now;
        }
    }

    /// <summary>Flies to a target (physical pixels) on the next <see cref="Update"/>, then waits there.</summary>
    public void PointAt(double x, double y, string? label = null)
    {
        _bubble = string.IsNullOrWhiteSpace(label) ? Phrases[_phrase++ % Phrases.Length] : label.Trim();
        _pendingTarget = (x, y);
        _flightRequested = true;
    }

    /// <summary>Stops pointing and goes back to trailing the mouse.</summary>
    public void ReturnToMouse()
    {
        _flightRequested = false;
        if (_phase is CursorPhase.Flying or CursorPhase.Pointing)
        {
            _phase = CursorPhase.Following;
        }
    }

    /// <param name="now">Monotonic time of this frame.</param>
    /// <param name="mouseX">Mouse position, physical pixels.</param>
    /// <param name="mouseY">Mouse position, physical pixels.</param>
    /// <param name="scale">Display scale under the mouse (1.0 = 96 DPI).</param>
    public CursorPose Update(TimeSpan now, double mouseX, double mouseY, double scale)
    {
        var seconds = _lastUpdate is { } last ? Math.Clamp((now - last).TotalSeconds, 0, MaximumFrameSeconds) : 0;
        _lastUpdate = now;

        if (!_initialised)
        {
            _position.SnapTo(mouseX + (FollowOffsetX * scale), mouseY + (FollowOffsetY * scale));
            _initialised = true;
        }

        if (_flightRequested)
        {
            StartFlight(now, scale);
        }

        DetectShake(now, mouseX, mouseY, scale);

        var flightScale = 1.0;
        switch (_phase)
        {
            case CursorPhase.Following:
                _position.Advance(mouseX + (FollowOffsetX * scale), mouseY + (FollowOffsetY * scale), seconds, Springs.CursorFollow);
                SettleRotation(seconds);
                break;

            case CursorPhase.Flying:
                var pose = _flight!.PoseAt(now - _phaseStartedAt);
                _position.SnapTo(pose.X, pose.Y);
                _rotation = pose.RotationDegrees;
                _rotationVelocity = 0;
                flightScale = pose.Scale;
                if (now - _phaseStartedAt >= _flight.Duration)
                {
                    Enter(CursorPhase.Pointing, now);
                    _mouseAtLanding = (mouseX, mouseY);
                }

                break;

            case CursorPhase.Pointing:
                SettleRotation(seconds);
                if (Distance(_mouseAtLanding, (mouseX, mouseY)) > ReturnDistance * scale)
                {
                    Enter(CursorPhase.Following, now);
                }

                break;

            case CursorPhase.Detached:
                if (!IsListening && now - _phaseStartedAt >= (_clicked ? ClickedDetachDuration : DetachDuration))
                {
                    Enter(CursorPhase.Following, now);
                }

                break;
        }

        // Rises with the voice at once, falls back gently, like a VU meter.
        _level = Math.Max(_levelTarget, _level * Math.Exp(-seconds / LevelFallSeconds));

        return new CursorPose(
            _position.X,
            _position.Y,
            _rotation,
            flightScale,
            _thinkingSince is { } since ? (now - since).TotalSeconds : null,
            IsSettled(mouseX, mouseY, scale))
        {
            ListeningLevel = IsListening ? Math.Round(_level, 2) : null,
            Bubble = _phase == CursorPhase.Pointing ? _bubble : null,
        };
    }

    private void StartFlight(TimeSpan now, double scale)
    {
        _flightRequested = false;
        _flight = new FlightPath(
            _position.X,
            _position.Y,
            _pendingTarget.X + (TargetOffsetX * scale),
            _pendingTarget.Y + (TargetOffsetY * scale));
        Enter(CursorPhase.Flying, now);
    }

    private void DetectShake(TimeSpan now, double mouseX, double mouseY, double scale)
    {
        if (!ShakeToDetachEnabled || _phase != CursorPhase.Following || IsThinking || IsListening)
        {
            _shakeDetector.Reset();
            return;
        }

        if (_shakeDetector.AddSample(mouseX / scale, mouseY / scale, now))
        {
            Enter(CursorPhase.Detached, now);
        }
    }

    private void SettleRotation(double seconds)
    {
        (var offset, _rotationVelocity) = Springs.Control.Step(_rotation - RestingRotation, _rotationVelocity, seconds);
        _rotation = RestingRotation + offset;
    }

    private bool IsSettled(double mouseX, double mouseY, double scale)
        => !IsThinking
           && !IsListening
           && Math.Abs(_rotation - RestingRotation) < 0.1
           && _phase switch
           {
               CursorPhase.Following => _position.IsSettled(mouseX + (FollowOffsetX * scale), mouseY + (FollowOffsetY * scale)),
               CursorPhase.Flying => false,
               _ => true,
           };

    private void Enter(CursorPhase phase, TimeSpan now)
    {
        _phase = phase;
        _phaseStartedAt = now;
        _clicked = false;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}

public enum CursorPhase
{
    /// <summary>Trailing the mouse.</summary>
    Following,

    /// <summary>On its way to a point it was asked to show.</summary>
    Flying,

    /// <summary>Waiting at the point until the user moves the mouse away.</summary>
    Pointing,

    /// <summary>Left in place by a mouse shake; re-attaches by itself.</summary>
    Detached,
}

/// <param name="X">Cursor centre, physical pixels.</param>
/// <param name="Y">Cursor centre, physical pixels.</param>
/// <param name="RotationDegrees">Clockwise; 0 = tip up.</param>
/// <param name="Scale">Extra scale on top of the display scale (mid-flight swell).</param>
/// <param name="ThinkingSeconds">Time since thinking started, or <see langword="null"/> when not thinking.</param>
/// <param name="IsSettled">Nothing is moving; the renderer may skip frames until the mouse moves.</param>
public readonly record struct CursorPose(double X, double Y, double RotationDegrees, double Scale, double? ThinkingSeconds, bool IsSettled)
{
    /// <summary>Smoothed microphone level while listening (draw <see cref="ListeningBars"/>), else <see langword="null"/>.</summary>
    public double? ListeningLevel { get; init; }

    /// <summary>Text for the bubble beside the cursor once it has arrived where it points.</summary>
    public string? Bubble { get; init; }
}
