using Jarvis.Contracts.Notch;

namespace Jarvis.Core.Notch;

/// <summary>
/// A form put aside keeps working behind a small pill: its progress updates the pill (and the
/// form kept for coming back), not the panel; when the notch would rest, the pill stays; starting
/// something new (talking, typing) lets go of it, the form's run carrying on.
/// </summary>
public static class NotchParking
{
    /// <summary>What to show for an incoming state, and what stays put aside; Show null keeps what's showing.</summary>
    public static (NotchState? Show, NotchState? Parked) Next(NotchState incoming, NotchState? parked, NotchState current)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (parked is NotchState.Form && incoming is NotchState.Listening or NotchState.Dictating or NotchState.Composing)
        {
            return (incoming, null);
        }

        if (parked is NotchState.Form kept)
        {
            if (incoming is NotchState.Form update)
            {
                return current is NotchState.IdleState or NotchState.Parked or NotchState.Working ? (PillFor(update), update) : (null, update);
            }

            if (incoming is NotchState.IdleState)
            {
                return (PillFor(kept), kept);
            }
        }

        return (incoming, parked);
    }

    /// <summary>The pill for a form put aside: its own status line, moving while it's still working.</summary>
    public static NotchState.Parked PillFor(NotchState.Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var busy = form.FillProgress is not null || form.Status.StartsWith("Answering", StringComparison.Ordinal) || form.Status.StartsWith("Filling", StringComparison.Ordinal);
        var status = form.Status.Split(" · ")[0].TrimEnd('.', '…');
        return new NotchState.Parked(busy ? status : $"Form ready · {status}", busy);
    }
}
