namespace Jarvis.Core.Motion;

/// <summary>A 2D position that chases a target on a <see cref="Spring"/>; one per animated point.</summary>
public struct SpringPoint
{
    public SpringPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; private set; }

    public double Y { get; private set; }

    public double VelocityX { get; private set; }

    public double VelocityY { get; private set; }

    public void Advance(double targetX, double targetY, double seconds, Spring spring)
    {
        (var offsetX, VelocityX) = spring.Step(X - targetX, VelocityX, seconds);
        (var offsetY, VelocityY) = spring.Step(Y - targetY, VelocityY, seconds);
        X = targetX + offsetX;
        Y = targetY + offsetY;
    }

    /// <summary>Jumps to a position and stops, e.g. when the cursor changes monitor.</summary>
    public void SnapTo(double x, double y)
    {
        X = x;
        Y = y;
        VelocityX = 0;
        VelocityY = 0;
    }

    public readonly bool IsSettled(double targetX, double targetY, double distanceTolerance = 0.5, double speedTolerance = 1)
        => Math.Abs(X - targetX) < distanceTolerance
           && Math.Abs(Y - targetY) < distanceTolerance
           && Math.Sqrt((VelocityX * VelocityX) + (VelocityY * VelocityY)) < speedTolerance;
}
