namespace Jarvis.Core.Motion;

/// <summary>
/// A damped spring described the SwiftUI way: <see cref="Response"/> (seconds, roughly one
/// oscillation) and <see cref="DampingFraction"/> (1 = critically damped, &lt;1 = bouncy).
/// <see cref="Step"/> uses the exact closed-form solution, so motion is identical at any frame
/// rate and never drifts the way Euler integration does.
/// </summary>
public readonly record struct Spring
{
    public Spring(double response, double dampingFraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(response);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dampingFraction);
        Response = response;
        DampingFraction = dampingFraction;
    }

    public double Response { get; }

    public double DampingFraction { get; }

    /// <summary>Undamped angular frequency ω = 2π / response.</summary>
    public double AngularFrequency => 2 * Math.PI / Response;

    /// <summary>
    /// Value for <c>Spring*NaturalMotionAnimation.Period</c> when the compositor runs the spring.
    /// Response / 2π is the mapping the reference app ships (tuned by eye), not a physical identity.
    /// </summary>
    public TimeSpan CompositionPeriod => TimeSpan.FromSeconds(Response / (2 * Math.PI));

    /// <summary>
    /// Advances a spring by <paramref name="seconds"/>.
    /// </summary>
    /// <param name="offset">Current position minus target.</param>
    /// <param name="velocity">Current velocity (units per second).</param>
    /// <param name="seconds">Elapsed time; negative values are treated as zero.</param>
    /// <returns>The new offset from target and the new velocity.</returns>
    public (double Offset, double Velocity) Step(double offset, double velocity, double seconds)
    {
        var t = Math.Max(0, seconds);
        var omega = AngularFrequency;
        var zeta = DampingFraction;

        if (zeta < 1)
        {
            var decay = zeta * omega;
            var dampedOmega = omega * Math.Sqrt(1 - (zeta * zeta));
            var envelope = Math.Exp(-decay * t);
            var cos = Math.Cos(dampedOmega * t);
            var sin = Math.Sin(dampedOmega * t);
            var c = (velocity + (decay * offset)) / dampedOmega;
            return (
                envelope * ((offset * cos) + (c * sin)),
                envelope * ((velocity * cos) - (((decay * c) + (offset * dampedOmega)) * sin)));
        }

        if (zeta == 1)
        {
            var envelope = Math.Exp(-omega * t);
            var b = velocity + (omega * offset);
            return (envelope * (offset + (b * t)), envelope * (velocity - (omega * b * t)));
        }

        var root = omega * Math.Sqrt((zeta * zeta) - 1);
        var r1 = (-zeta * omega) + root;
        var r2 = (-zeta * omega) - root;
        var c1 = (velocity - (r2 * offset)) / (r1 - r2);
        var c2 = offset - c1;
        var e1 = Math.Exp(r1 * t);
        var e2 = Math.Exp(r2 * t);
        return ((c1 * e1) + (c2 * e2), (r1 * c1 * e1) + (r2 * c2 * e2));
    }
}
