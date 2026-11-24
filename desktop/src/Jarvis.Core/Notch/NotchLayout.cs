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

    /// <summary>The empty chat's middle: a line saying what to do, between the switch and the bar.</summary>
    public const double EmptyChatSpace = 64;

    /// <summary>The chat with history grows to this.</summary>
    public const double ChatMaxHeight = 440;

    public static NotchFrame FrameFor(NotchState state) => state switch
    {
        NotchState.IdleState => Sliver(Springs.NotchClose),
        NotchState.Working => Sliver(Springs.NotchClose),
        NotchState.Listening or NotchState.Dictating => new NotchFrame(ActivityWidth, ActivityHeight, 18, IsSliver: false, Spring: null),
        NotchState.Thinking or NotchState.Speaking or NotchState.Parked => new NotchFrame(ActivityWidth, ActivityHeight, 18, IsSliver: false, Springs.NotchOpen),
        NotchState.Composing composing => new NotchFrame(ExpandedWidth, ComposerFrameHeight(composing.History ?? []), 22, IsSliver: false, Springs.TextInputOpen),
        NotchState.Response response => ResponseFrame(response),
        NotchState.Form or NotchState.EventDraft => new NotchFrame(ExpandedWidth, MaxExpandedHeight, 22, IsSliver: false, Springs.NotchOpen),
        NotchState.Settings => new NotchFrame(SettingsWidth, SettingsHeight, 22, IsSliver: false, Springs.NotchOpen),
        NotchState.Home home => HomeFrame(home),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown notch state."),
    };

    /// <summary>The widest a panel grows to fit its buttons: as wide as Settings in the notch.</summary>
    public const double MaxPanelWidth = SettingsWidth;

    /// <summary>Shoulders and side padding: what a panel's width holds besides its content.</summary>
    public const double PanelChrome = (2 * ShoulderRadius) + (2 * ContentHorizontalPadding);

    public const double ActionSpacing = 8;
    public const double HomePillSpacing = 6;

    /// <summary>A home pill is 34 high; wrapped rows sit this far apart.</summary>
    public const double HomePillRowHeight = 34 + HomePillSpacing;

    /// <summary>
    /// A 12-pt label's width, estimated generously (most Segoe UI letters are narrower), so a row
    /// laid out by it always fits on screen.
    /// </summary>
    /// <remarks>ponytail: an estimate keeps Core free of the UI; measure the real buttons if labels ever clip.</remarks>
    private static double LabelWidth(string label) => label.Length * 7.5;

    /// <summary>A reply's button: 12 DIP padding each side, at least 64 wide (DESIGN-FACTS.md §2.1).</summary>
    public static double ActionWidth(string label) => Math.Max(64, LabelWidth(label) + 24 + 2);

    /// <summary>A home pill: 14 DIP padding each side, the 13-pt icon and an 8 DIP gap before the title.</summary>
    public static double HomePillWidth(string title) => 14 + 13 + 8 + LabelWidth(title) + 14 + 2;

    /// <summary>
    /// Wide enough for every item in one row (never narrower than the usual panel, never wider than
    /// <see cref="MaxPanelWidth"/>); what still doesn't fit wraps onto more rows (<see cref="Rows"/>).
    /// </summary>
    public static double WidthToFit(IReadOnlyList<double> items, double spacing)
    {
        ArgumentNullException.ThrowIfNull(items);
        var row = items.Sum() + (spacing * Math.Max(0, items.Count - 1)) + PanelChrome;
        return Math.Clamp(row, ExpandedWidth, MaxPanelWidth);
    }

    /// <summary>How many items go on each row, filling a row before starting the next. An item wider than the room gets a row of its own.</summary>
    public static IReadOnlyList<int> Rows(IReadOnlyList<double> items, double spacing, double available)
    {
        ArgumentNullException.ThrowIfNull(items);
        var rows = new List<int>();
        double used = 0;
        foreach (var width in items)
        {
            if (rows.Count > 0 && used + spacing + width <= available)
            {
                rows[^1]++;
                used += spacing + width;
            }
            else
            {
                rows.Add(1);
                used = width;
            }
        }

        return rows;
    }

    private static NotchFrame ResponseFrame(NotchState.Response response)
    {
        var buttons = (response.Actions ?? []).Select(action => ActionWidth(action.Label)).ToList();
        var width = WidthToFit(buttons, ActionSpacing);
        var bar = Rows(buttons, ActionSpacing, width - PanelChrome).Count * ActionBarHeight;
        var body = response.Card is { } card ? CardHeight(response.Text, card, width) : ResponseHeight(response.Text, width);
        return new NotchFrame(width, Math.Min(MaxExpandedHeight, body + bar), 22, IsSliver: false, Springs.NotchOpen);
    }

    private static NotchFrame HomeFrame(NotchState.Home home)
    {
        var pills = home.Actions.Select(item => HomePillWidth(item.Title)).ToList();
        var width = WidthToFit(pills, HomePillSpacing);
        var extraRows = Math.Max(0, Rows(pills, HomePillSpacing, width - PanelChrome).Count - 1);
        return new NotchFrame(width, HomeHeight + (extraRows * HomePillRowHeight) + (home.Recent.Count > 0 ? HomeCardsHeight : 0), 22, IsSliver: false, Springs.NotchOpen);
    }

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
            return ComposerHeight + EmptyChatSpace; // room to breathe, with a line in the middle
        }

        // Each exchange: a time label, the question's bubble (right), the answer's bubble (left).
        static double Lines(string text, double perLine) => Math.Max(1, Math.Ceiling(text.Length / perLine));
        var chat = history.Sum(turn => 22 + (Lines(turn.Question, 52) * 17) + 18 + (Lines(turn.Answer, 64) * LineHeight) + 18 + 16);
        return Math.Min(ChatMaxHeight, chat + ComposerHeight);
    }

    /// <summary>A card's own height: its label, the text at 15 pt, the footnote at 11.5 pt, snug (no three-line minimum).</summary>
    public static double CardHeight(string text, ResponseCard card, double width)
    {
        ArgumentNullException.ThrowIfNull(card);
        var room = (width - PanelChrome - 22) / (ExpandedWidth - PanelChrome - 22);
        static int Lines(string value, double perLine) => value.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(Math.Max(line.Length, 1) / perLine)));
        var height = CardTop + CardLabelHeight + (Lines(text.Trim(), 60 * room) * CardLineHeight);
        if (card.Footnote is { Length: > 0 } footnote)
        {
            height += 6 + (Lines(footnote, 84 * room) * 16);
        }

        return height + CardBottom;
    }

    private const double CardTop = 12;
    private const double CardLabelHeight = 22;
    private const double CardLineHeight = 21;
    private const double CardBottom = 14;

    /// <summary>Grows with wrapped lines (≈74 characters each at the usual width, more when wider), at least three lines, capped at the max height.</summary>
    private static double ResponseHeight(string text, double width)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return 70;
        }

        var perLine = CharactersPerLine * (width - PanelChrome) / (ExpandedWidth - PanelChrome);
        var lines = trimmed.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(Math.Max(line.Length, 1) / perLine)));
        return Math.Min(MaxExpandedHeight, 56 + (LineHeight * Math.Max(3, lines)) + 32);
    }
}

/// <param name="Width">Outer width including the shoulders.</param>
/// <param name="Height">Height below the top edge (or below the gap when floating).</param>
/// <param name="BottomRadius">Requested bottom corner radius before capping.</param>
/// <param name="IsSliver">Floats below the edge as a pill instead of hugging it.</param>
/// <param name="Spring">Motion into this frame; <see langword="null"/> snaps instantly (listening must feel immediate).</param>
public sealed record NotchFrame(double Width, double Height, double BottomRadius, bool IsSliver, Spring? Spring);
