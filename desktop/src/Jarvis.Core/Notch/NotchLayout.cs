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

    /// <summary>Settings in the notch: the sections beside a page, as roomy as the window's.</summary>
    public const double SettingsWidth = 800;

    public const double SettingsHeight = 560;
    public const double EnvelopeWidth = 820;
    public const double SliverTopGap = 4;
    public const double ShoulderRadius = 10;
    public const double ContentHorizontalPadding = 18;

    public const double SliverWidth = 60;
    public const double SliverHeight = 8;
    private const double ActivityWidth = 360;
    private const double ActivityHeight = 32;
    private const double CharactersPerLine = 74;
    private const double LineHeight = 18;

    /// <summary>The home's greeting and tiles; each recent item adds a row.</summary>
    /// <summary>Greeting 24 + hint 18 + gaps + the pill bar 34 + padding; recent work adds a row of cards.</summary>
    public const double HomeHeight = 14 + 24 + 18 + 14 + 34 + 14;

    public const double HomeRowHeight = 34;

    /// <summary>The row of recent-work cards on the home.</summary>
    public const double HomeCardsHeight = 88 + 12;

    /// <summary>Action buttons are 24 DIPs tall (DESIGN-FACTS.md §2.1), plus a gap above.</summary>
    public const double ActionBarHeight = 24 + 8;

    /// <summary>The Home/Chat switch row 46, then the chat's bar (talk pill, text box, send) 38 high and 14 below.</summary>
    private const double ComposerHeight = 46 + 38 + 14;

    /// <summary>The chat with history grows to this.</summary>
    public const double ChatMaxHeight = 440;

    public static NotchFrame FrameFor(NotchState state) => state switch
    {
        NotchState.IdleState => Sliver(Springs.NotchClose),
        NotchState.Working => Sliver(Springs.NotchClose),
        NotchState.Listening or NotchState.Dictating => new NotchFrame(ActivityWidth, ActivityHeight, 18, IsSliver: false, Spring: null),
        NotchState.Thinking or NotchState.Speaking or NotchState.Parked => new NotchFrame(ActivityWidth, ActivityHeight, 18, IsSliver: false, Springs.NotchOpen),
        NotchState.Composing composing => new NotchFrame(ExpandedWidth, ComposerFrameHeight(composing.History ?? []), 22, IsSliver: false, Springs.TextInputOpen),
        NotchState.Response response => new NotchFrame(
            ExpandedWidth,
            Math.Min(MaxExpandedHeight, ResponseHeight(response.Text) + (response.Actions is { Count: > 0 } ? ActionBarHeight : 0)),
            22,
            IsSliver: false,
            Springs.NotchOpen),
        NotchState.Form or NotchState.EventDraft => new NotchFrame(ExpandedWidth, MaxExpandedHeight, 22, IsSliver: false, Springs.NotchOpen),
        NotchState.Settings => new NotchFrame(SettingsWidth, SettingsHeight, 22, IsSliver: false, Springs.NotchOpen),
        NotchState.Home home => new NotchFrame(ExpandedWidth, HomeHeight + (home.Recent.Count > 0 ? HomeCardsHeight : 0), 22, IsSliver: false, Springs.NotchOpen),
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

        // Each exchange: a time label, the question's bubble (right), the answer's bubble (left).
        static double Lines(string text, double perLine) => Math.Max(1, Math.Ceiling(text.Length / perLine));
        var chat = history.Sum(turn => 22 + (Lines(turn.Question, 52) * 17) + 18 + (Lines(turn.Answer, 64) * LineHeight) + 18 + 16);
        return Math.Min(ChatMaxHeight, chat + ComposerHeight);
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
