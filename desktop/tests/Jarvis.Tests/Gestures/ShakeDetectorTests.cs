using Jarvis.Core.Gestures;

namespace Jarvis.Tests.Gestures;

public sealed class ShakeDetectorTests
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(18);

    [Fact]
    public void VigorousShake_IsDetected()
    {
        var detector = new ShakeDetector();

        Assert.Contains(true, Feed(detector, ShakePath(amplitude: 70, swings: 16, jitter: 6)));
    }

    [Fact]
    public void SmallTremor_IsIgnored()
    {
        var detector = new ShakeDetector();

        Assert.DoesNotContain(true, Feed(detector, ShakePath(amplitude: 1.5, swings: 30, jitter: 0)));
    }

    [Fact]
    public void ShortShake_WithoutEnoughTravel_IsIgnored()
    {
        var detector = new ShakeDetector();

        Assert.DoesNotContain(true, Feed(detector, ShakePath(amplitude: 5, swings: 16, jitter: 0)));
    }

    [Fact]
    public void StraightFastMove_IsIgnored()
    {
        var detector = new ShakeDetector();
        var straight = Enumerable.Range(0, 30).Select(i => (X: i * 60.0, Y: 0.0));

        Assert.DoesNotContain(true, Feed(detector, straight));
    }

    [Fact]
    public void ContinuousShaking_FiresOncePerCooldown()
    {
        var detector = new ShakeDetector();

        var detections = Feed(detector, ShakePath(amplitude: 70, swings: 40, jitter: 6)).Count(detected => detected);

        Assert.Equal(1, detections);
    }

    private static IEnumerable<(double X, double Y)> ShakePath(double amplitude, int swings, double jitter)
        => Enumerable.Range(0, swings).Select(i => (500 + (i % 2 == 0 ? amplitude : -amplitude), 300 + (i % 3 == 0 ? jitter : -jitter)));

    private static List<bool> Feed(ShakeDetector detector, IEnumerable<(double X, double Y)> points)
        => points.Select((point, index) => detector.AddSample(point.X, point.Y, FrameInterval * (index + 1))).ToList();
}
