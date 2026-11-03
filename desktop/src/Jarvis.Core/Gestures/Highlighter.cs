using Jarvis.Contracts.Overlay;

namespace Jarvis.Core.Gestures;

/// <summary>
/// Hold the talk keys and circle something: the mouse leaves a highlighter trail, and the turn
/// knows which area you meant. Starts after 8 points of movement, keeps points at least 1.5 apart;
/// holding still for 0.35 s within 12 points counts as pointing there (DESIGN-FACTS.md §3).
/// Positions are physical pixels; distances are DIPs × display scale.
/// </summary>
public sealed class Highlighter
{
    private const double ActivationDips = 8;
    private const double SpacingDips = 1.5;
    private const double DwellRadiusDips = 12;
    private static readonly TimeSpan DwellTime = TimeSpan.FromSeconds(0.35);

    private readonly List<(double X, double Y)> _path = [];
    private (double X, double Y) _origin;
    private (double X, double Y) _dwellAnchor;
    private TimeSpan _dwellSince;
    private (double X, double Y)? _dwell;
    private double _scale = 1;
    private bool _recording;

    /// <summary>The trail so far (empty until it has activated).</summary>
    public IReadOnlyList<(double X, double Y)> Path => IsDrawing ? _path : [];

    public bool IsDrawing { get; private set; }

    public void Start(double x, double y, double scale, TimeSpan now)
    {
        _path.Clear();
        _path.Add((x, y));
        _origin = _dwellAnchor = (x, y);
        _dwellSince = now;
        _dwell = null;
        _scale = scale;
        IsDrawing = false;
        _recording = true;
    }

    /// <summary>Feed the mouse position each frame while the keys are held.</summary>
    public void Add(double x, double y, TimeSpan now)
    {
        if (!_recording)
        {
            return;
        }

        if (Distance((x, y), _dwellAnchor) > DwellRadiusDips * _scale)
        {
            _dwellAnchor = (x, y);
            _dwellSince = now;
        }
        else if (now - _dwellSince >= DwellTime)
        {
            _dwell = _dwellAnchor;
        }

        IsDrawing |= Distance((x, y), _origin) >= ActivationDips * _scale;
        if (Distance((x, y), _path[^1]) >= SpacingDips * _scale)
        {
            _path.Add((x, y));
        }
    }

    /// <summary>Stops; what the user marked, or <see langword="null"/> if they neither moved nor held still.</summary>
    public ScreenHighlight? Finish()
    {
        if (!_recording)
        {
            return null;
        }

        _recording = false;
        if (IsDrawing)
        {
            return new ScreenHighlight(
                _path.Min(point => point.X), _path.Min(point => point.Y),
                _path.Max(point => point.X), _path.Max(point => point.Y),
                IsPoint: false);
        }

        return _dwell is { } held ? new ScreenHighlight(held.X, held.Y, held.X, held.Y, IsPoint: true) : null;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
