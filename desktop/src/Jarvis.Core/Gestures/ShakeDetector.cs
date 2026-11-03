namespace Jarvis.Core.Gestures;

/// <summary>
/// Recognises a deliberate mouse shake (rapid back-and-forth) from a stream of pointer samples.
/// Counts direction reversals on each axis inside a sliding window and requires enough travel
/// that trembling hands and small corrections never trigger it.
/// </summary>
public sealed class ShakeDetector
{
    private readonly ShakeDetectorOptions _options;
    private readonly Queue<Sample> _samples = new();
    private TimeSpan? _lastSampleAt;
    private TimeSpan? _lastShakeAt;

    public ShakeDetector(ShakeDetectorOptions? options = null)
    {
        _options = options ?? ShakeDetectorOptions.Default;
    }

    /// <summary>Feeds one pointer position.</summary>
    /// <param name="x">Horizontal position in DIPs (divide physical pixels by the display scale).</param>
    /// <param name="y">Vertical position in DIPs.</param>
    /// <param name="timestamp">Monotonic time of the sample.</param>
    /// <returns><see langword="true"/> exactly once per detected shake.</returns>
    public bool AddSample(double x, double y, TimeSpan timestamp)
    {
        if (timestamp - _lastSampleAt < _options.MinimumSampleInterval)
        {
            return false;
        }

        _lastSampleAt = timestamp;
        _samples.Enqueue(new Sample(x, y, timestamp));
        while (_samples.Peek().Timestamp < timestamp - _options.Window)
        {
            _samples.Dequeue();
        }

        if (_samples.Count < _options.MinimumSamples || timestamp - _lastShakeAt < _options.Cooldown)
        {
            return false;
        }

        if (!IsShake())
        {
            return false;
        }

        _lastShakeAt = timestamp;
        _samples.Clear();
        return true;
    }

    public void Reset()
    {
        _samples.Clear();
        _lastSampleAt = null;
    }

    private bool IsShake()
    {
        var reversals = 0;
        var pathLength = 0.0;
        var directionX = 0;
        var directionY = 0;
        Sample? previous = null;

        foreach (var sample in _samples)
        {
            if (previous is { } p)
            {
                var dx = sample.X - p.X;
                var dy = sample.Y - p.Y;
                pathLength += Math.Sqrt((dx * dx) + (dy * dy));
                reversals += CountReversal(dx, ref directionX) + CountReversal(dy, ref directionY);
            }

            previous = sample;
        }

        return reversals >= _options.MinimumReversals && pathLength >= _options.MinimumPathLength;
    }

    private int CountReversal(double delta, ref int direction)
    {
        if (Math.Abs(delta) <= _options.NoiseFloor)
        {
            return 0;
        }

        var newDirection = Math.Sign(delta);
        var reversed = direction != 0 && newDirection != direction;
        direction = newDirection;
        return reversed ? 1 : 0;
    }

    private readonly record struct Sample(double X, double Y, TimeSpan Timestamp);
}

/// <summary>Tuning for <see cref="ShakeDetector"/>; defaults match the reference app.</summary>
public sealed record ShakeDetectorOptions
{
    public static ShakeDetectorOptions Default { get; } = new();

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(0.5);

    public TimeSpan MinimumSampleInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(1);

    public int MinimumSamples { get; init; } = 4;

    /// <summary>Per-sample movement (DIPs) below which a direction change is ignored.</summary>
    public double NoiseFloor { get; init; } = 2;

    /// <summary>Direction flips counted across both axes.</summary>
    public int MinimumReversals { get; init; } = 6;

    /// <summary>Total distance travelled inside the window (DIPs).</summary>
    public double MinimumPathLength { get; init; } = 260;
}
