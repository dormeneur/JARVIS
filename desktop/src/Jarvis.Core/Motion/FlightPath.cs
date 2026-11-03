namespace Jarvis.Core.Motion;

/// <summary>
/// The arc the companion cursor flies along when pointing at something: a quadratic Bézier that
/// bows upward, eased with smoothstep, nose following the tangent and swelling mid-flight.
/// </summary>
public sealed class FlightPath
{
    private const double PointsPerSecond = 800;
    private const double MaximumSeconds = 1.4;
    private const double ArcHeightPerDistance = 0.2;
    private const double MaximumArcHeight = 80;
    private const double MidFlightScaleBoost = 0.3;

    private readonly double _startX;
    private readonly double _startY;
    private readonly double _controlX;
    private readonly double _controlY;
    private readonly double _endX;
    private readonly double _endY;

    public FlightPath(double startX, double startY, double endX, double endY, double minimumSeconds = 0.6)
    {
        _startX = startX;
        _startY = startY;
        _endX = endX;
        _endY = endY;

        var distance = Math.Sqrt(((endX - startX) * (endX - startX)) + ((endY - startY) * (endY - startY)));
        Duration = TimeSpan.FromSeconds(Math.Clamp(distance / PointsPerSecond, minimumSeconds, Math.Max(minimumSeconds, MaximumSeconds)));

        _controlX = (startX + endX) / 2;
        _controlY = ((startY + endY) / 2) - Math.Min(distance * ArcHeightPerDistance, MaximumArcHeight);
    }

    public TimeSpan Duration { get; }

    /// <summary>Pose at <paramref name="elapsed"/> since take-off; clamps to the landing pose.</summary>
    public FlightPose PoseAt(TimeSpan elapsed)
    {
        var linear = Math.Clamp(elapsed / Duration, 0, 1);
        var t = linear * linear * (3 - (2 * linear));
        var u = 1 - t;

        var x = (u * u * _startX) + (2 * u * t * _controlX) + (t * t * _endX);
        var y = (u * u * _startY) + (2 * u * t * _controlY) + (t * t * _endY);

        var tangentX = (2 * u * (_controlX - _startX)) + (2 * t * (_endX - _controlX));
        var tangentY = (2 * u * (_controlY - _startY)) + (2 * t * (_endY - _controlY));
        var rotation = (Math.Atan2(tangentY, tangentX) * (180 / Math.PI)) + 90;

        var scale = 1 + (Math.Sin(linear * Math.PI) * MidFlightScaleBoost);
        return new FlightPose(x, y, rotation, scale);
    }
}

/// <param name="RotationDegrees">Clockwise; 0 means the cursor's tip points straight up.</param>
public readonly record struct FlightPose(double X, double Y, double RotationDegrees, double Scale);
