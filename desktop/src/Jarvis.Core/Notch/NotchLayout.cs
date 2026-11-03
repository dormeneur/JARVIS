using Jarvis.Contracts.Notch;
using Jarvis.Core.Motion;

namespace Jarvis.Core.Notch;

/// <summary>
/// How big the notch is, how round, whether it floats or hugs the top edge, and which spring
/// carries it there, for each <see cref="NotchState"/>. All sizes are DIPs.
/// Values match the reference app (DESIGN-FACTS.md §2.1).
/// </summary>
public static class NotchLayout
{
    public const double ExpandedWidth = 532;
    public const double MaxExpandedHeight = 280;
    public const double EnvelopeWidth = 820;
    public const double SliverTopGap = 4;
    public const double ShoulderRadius = 10;
    public const double ContentHorizontalPadding = 18;

    private const double SliverWidth = 60;
    private const double SliverHeight = 8;
    private const double ActivityWidth = 360;
    private const double ActivityHeight = 32;
    private const double CharactersPerLine = 74;
    private const double LineHeight = 18;

    /// <summary>Action buttons are 24 DIPs tall (DESIGN-FACTS.md §2.1), plus a gap above.</summary>
    public const double ActionBarHeight = 24 + 8;

    /// <summary>Text-input top padding 34 + one 16 DIP input line with 8 above and below + 16 bottom gap.</summary>
    private const double ComposerHeight = 34 + 8 + 16 + 8 + 16;

    public static NotchFrame FrameFor(NotchState state) => state switch
    {
        NotchState.IdleState => Sliver(Springs.NotchClose),
        NotchState.Working => Sliver(Springs.NotchClose),
        NotchState.Listening or NotchState.Dictating => new NotchFrame(ActivityWidth, ActivityHeight, 18, IsSliver: false, Spring: null),
        NotchState.Thinking or NotchState.Speaking => new NotchFrame(ActivityWidth, ActivityHeight, 18, IsSliver: false, Springs.NotchOpen),
        NotchState.Composing composing => new NotchFrame(ExpandedWidth, ComposerFrameHeight(composing.History ?? []), 22, IsSliver: false, Springs.TextInputOpen),
        NotchState.Response response => new NotchFrame(
            ExpandedWidth,
            Math.Min(MaxExpandedHeight, ResponseHeight(response.Text) + (response.Actions is { Count: > 0 } ? ActionBarHeight : 0)),
            22,
            IsSliver: false,
            Springs.NotchOpen),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown notch state."),
    };

    /// <summary>The corner radius actually drawn: capped so short shapes stay pills.</summary>
    public static double EffectiveCornerRadius(NotchFrame frame)
    {
        var sliver = frame.IsSliver ? 1 : 0;
        var concavity = 1 - sliver;
        return (0.854 + (0.146 * sliver)) * Math.Min(frame.BottomRadius, Math.Min(frame.Width - (2 * ShoulderRadius * concavity), frame.Height) / 2);
    }

    private static NotchFrame Sliver(Spring spring) => new(SliverWidth, SliverHeight, 14, IsSliver: true, spring);

    /// <summary>The text box alone, or with the chat above it: grows with the wrapped lines, capped at the max height.</summary>
    private static double ComposerFrameHeight(IReadOnlyList<ChatTurn> history)
    {
        if (history.Count == 0)
        {
            return ComposerHeight;
        }

        static double Lines(string text, double perLine) => Math.Max(1, Math.Ceiling(text.Length / perLine));
        var chat = history.Sum(turn => (Lines(turn.Question, 62) * 16) + (Lines(turn.Answer, CharactersPerLine) * LineHeight) + 20);
        return Math.Min(MaxExpandedHeight, 14 + chat + 64);
    }

    /// <summary>Grows with wrapped lines (≈74 characters each), at least three lines, capped at the max height.</summary>
    private static double ResponseHeight(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return 70;
        }

        var lines = trimmed.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(Math.Max(line.Length, 1) / CharactersPerLine)));
        return Math.Min(MaxExpandedHeight, 56 + (LineHeight * Math.Max(3, lines)) + 32);
    }
}

/// <param name="Width">Outer width including the shoulders.</param>
/// <param name="Height">Height below the top edge (or below the gap when floating).</param>
/// <param name="BottomRadius">Requested bottom corner radius before capping.</param>
/// <param name="IsSliver">Floats below the edge as a pill instead of hugging it.</param>
/// <param name="Spring">Motion into this frame; <see langword="null"/> snaps instantly (listening must feel immediate).</param>
public sealed record NotchFrame(double Width, double Height, double BottomRadius, bool IsSliver, Spring? Spring);
