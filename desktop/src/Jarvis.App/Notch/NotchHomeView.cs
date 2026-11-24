using Jarvis.Contracts.Notch;
using Jarvis.Core.Notch;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jarvis.App.Notch;

/// <summary>
/// The notch's home: a greeting and how to talk, recent work as glass cards across the middle,
/// and a bar of pills to start things along the bottom. The mouse leaving it for a moment closes
/// it, as does never coming into it.
/// </summary>
internal sealed class NotchHomeView
{
    private const string IconFont = "Segoe Fluent Icons,Segoe MDL2 Assets";
    private const double Width = NotchLayout.ExpandedWidth - (2 * NotchLayout.ShoulderRadius) - (2 * NotchLayout.ContentHorizontalPadding);
    /// <summary>It holds this long after the pointer leaves (DESIGN-FACTS.md §3, notch hover dwell).</summary>
    private static readonly TimeSpan LeaveGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NeverEntered = TimeSpan.FromSeconds(8);

    private readonly TextBlock _greeting;
    private readonly TextBlock _hint;
    /// <summary>The job carousel: every card stacked in one cell, placed by its distance from the focused one.</summary>
    private readonly Grid _cards = new() { Height = CardHeight + 4, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<Button> _shownCards = [];
    private int _focus;

    /// <summary>Where the carousel is, in cards: the focused one while at rest, in between mid-swipe.</summary>
    private double _position;
    private readonly DispatcherQueueTimer _settle;
    private readonly Button _previous;
    private readonly Button _next;

    /// <summary>One click of a mouse wheel (WHEEL_DELTA).</summary>
    private const int WheelClick = 120;

    /// <summary>How far past either end a swipe may pull, in cards, before it springs back.</summary>
    private const double Overscroll = 0.35;

    private const double CardWidth = 216;
    private const double CardHeight = 84;

    /// <summary>How far apart neighbouring cards sit: the focused one whole, the others peeking out from behind.</summary>
    private const double CardStep = 172;
    private const float SideScale = 0.8f;
    /// <summary>Rows of pills: the notch widens to fit them (NotchLayout), and what still doesn't fit wraps.</summary>
    private readonly StackPanel _pills = new() { Orientation = Orientation.Vertical, Spacing = NotchLayout.HomePillSpacing, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DispatcherQueueTimer _leave;
    private NotchState.Home? _home;
    private bool _entered;

    public NotchHomeView(Grid area)
    {
        var font = Font;
        _greeting = new TextBlock { FontFamily = font, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center };
        _hint = new TextBlock { FontFamily = font, FontSize = 12, Foreground = Brush("TextSecondaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        View = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 14, NotchLayout.ContentHorizontalPadding, 14),
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // so pointer events reach it everywhere
            Children = { _greeting, _hint, _cards, _pills },
        };
        _cards.Margin = new Thickness(0, 10, 0, 2);
        _cards.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // the wheel works over the gaps too
        // A mouse wheel's click moves one card; a touchpad swipe follows the fingers, then settles on the nearest.
        _cards.PointerWheelChanged += (_, e) =>
        {
            var properties = e.GetCurrentPoint(_cards).Properties;
            var delta = properties.MouseWheelDelta;
            e.Handled = true;
            if (Math.Abs(delta) >= WheelClick && !properties.IsHorizontalMouseWheel)
            {
                Focus(_focus + (delta > 0 ? -1 : 1));
                return;
            }

            // Fingers to the left (a positive horizontal delta) bring the next card in, as on a phone.
            var cards = (properties.IsHorizontalMouseWheel ? delta : -delta) / (double)WheelClick;
            Track(_position + cards);
        };
        _settle = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settle.IsRepeating = false;
        _settle.Interval = TimeSpan.FromMilliseconds(140);
        _settle.Tick += (_, _) => Focus((int)Math.Round(_position));
        _previous = Arrow("", "Previous", HorizontalAlignment.Left, -1);
        _next = Arrow("", "Next", HorizontalAlignment.Right, 1);
        _pills.Margin = new Thickness(0, 10, 0, 0);
        area.Children.Add(View);

        _leave = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _leave.IsRepeating = false;
        _leave.Tick += (_, _) => _home?.Dismiss();
        View.PointerEntered += (_, _) =>
        {
            _entered = true;
            _leave.Stop();
        };
        View.PointerExited += (_, _) => Leave(LeaveGrace);
    }

    public StackPanel View { get; }

    private static FontFamily Font => (FontFamily)Application.Current.Resources["FontText"];

    public void Show(NotchState.Home home)
    {
        if (ReferenceEquals(home, _home))
        {
            return;
        }

        // A refresh of the same home (a job moved on) keeps whether the pointer is in it.
        var refresh = _home is not null;
        _home = home;
        if (!refresh)
        {
            _entered = false;
        }

        _greeting.Text = home.Greeting;
        _hint.Text = home.Hint;
        // Oldest on the left, newest in the middle; a refresh keeps the card the user scrolled to.
        var items = home.Recent.Reverse().ToList();
        var keep = refresh && _focus < _shownCards.Count ? _focus : int.MaxValue;
        _cards.Children.Clear();
        _cards.Children.Add(_previous);
        _cards.Children.Add(_next);
        _shownCards.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var at = i;
            var card = Card(items[i], CardWidth);
            card.Height = CardHeight;
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.CenterPoint = new System.Numerics.Vector3((float)CardWidth / 2, (float)CardHeight / 2, 0);
            card.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) };
            card.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) };
            card.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(260) };
            card.Tag = at;
            _shownCards.Add(card);
            _cards.Children.Add(card);
        }

        _cards.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Focus(Math.Min(keep, items.Count - 1), animate: false);
        _pills.Children.Clear();
        var width = NotchLayout.FrameFor(home).Width;
        var rows = NotchLayout.Rows([.. home.Actions.Select(item => NotchLayout.HomePillWidth(item.Title))], NotchLayout.HomePillSpacing, width - NotchLayout.PanelChrome);
        var placed = 0;
        foreach (var count in rows)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = NotchLayout.HomePillSpacing, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var item in home.Actions.Skip(placed).Take(count))
            {
                row.Children.Add(Pill(item));
            }

            placed += count;
            _pills.Children.Add(row);
        }

        View.Visibility = Visibility.Visible;
        if (!refresh)
        {
            Leave(NeverEntered);
        }
    }

    /// <summary>Brings a card to the middle: it grows to full size and comes forward; the rest shrink and fade by distance.</summary>
    private void Focus(int index, bool animate = true)
    {
        _settle.Stop();
        if (_shownCards.Count == 0)
        {
            UpdateArrows();
            return;
        }

        _focus = Math.Clamp(index, 0, _shownCards.Count - 1);
        _position = _focus;
        Layout(animate);
        UpdateArrows();
    }

    /// <summary>Mid-swipe: the cards follow the fingers exactly (no transitions), a little past the ends at most.</summary>
    private void Track(double position)
    {
        if (_shownCards.Count == 0)
        {
            return;
        }

        _position = Math.Clamp(position, -Overscroll, _shownCards.Count - 1 + Overscroll);
        Layout(animate: false);
        _settle.Stop();
        _settle.Start(); // fingers lifted (no more events for a moment): settle on the nearest card, animated
    }

    private void Layout(bool animate)
    {
        foreach (var (card, i) in _shownCards.Select((card, i) => (card, i)))
        {
            if (animate)
            {
                Place(card, i - _position);
                continue;
            }

            var (slide, grow, fade) = (card.TranslationTransition, card.ScaleTransition, card.OpacityTransition);
            card.TranslationTransition = null;
            card.ScaleTransition = null;
            card.OpacityTransition = null;
            Place(card, i - _position);
            card.TranslationTransition = slide;
            card.ScaleTransition = grow;
            card.OpacityTransition = fade;
        }
    }

    /// <summary>A card <paramref name="away"/> cards from the middle (in between while swiping): size and fade follow smoothly.</summary>
    private static void Place(Button card, double away)
    {
        var distance = Math.Abs(away);
        var scale = (float)(1 - ((1 - SideScale) * Math.Min(1, distance)));
        card.Translation = new System.Numerics.Vector3((float)(away * CardStep), 0, 0);
        card.Scale = new System.Numerics.Vector3(scale, scale, 1);
        card.Opacity = distance switch
        {
            <= 1 => 1 - (0.4 * distance),
            <= 2 => 0.6 - (0.35 * (distance - 1)),
            <= 3 => 0.25 * (3 - distance),
            _ => 0,
        };
        card.IsHitTestVisible = distance <= 1.5;
        Canvas.SetZIndex(card, 10 - (int)Math.Round(distance));
    }

    /// <summary>‹ and › at the carousel's edges: shown with more than one card, dimmed at either end.</summary>
    private Button Arrow(string glyph, string name, HorizontalAlignment side, int step)
    {
        var arrow = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = Glass,
            BorderBrush = GlassEdge,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFont), FontSize = 10, Foreground = Brush("TextPrimaryBrush") },
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetName(arrow, name);
        ToolTipService.SetToolTip(arrow, name);
        Hover(arrow);
        Canvas.SetZIndex(arrow, 100); // above every card
        arrow.Click += (_, _) => Focus(_focus + step);
        _cards.Children.Add(arrow);
        return arrow;
    }

    private void UpdateArrows()
    {
        var shown = _shownCards.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _previous.Visibility = shown;
        _next.Visibility = shown;
        _previous.IsEnabled = _focus > 0;
        _next.IsEnabled = _focus < _shownCards.Count - 1;
    }

    public void Hide()
    {
        _leave.Stop();
        _home = null;
        View.Visibility = Visibility.Collapsed;
    }

    private void Leave(TimeSpan after)
    {
        if (_home is null || (after == NeverEntered && _entered))
        {
            return;
        }

        _leave.Interval = after;
        _leave.Start();
    }

    /// <summary>A recent piece of work: its icon and state on top, its name below (two lines).</summary>
    private Button Card(HomeItem item, double width)
    {
        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        state.Children.Add(new FontIcon { Glyph = item.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 12, Foreground = Brush("TextSecondaryBrush") });
        if (item.Detail is { } detail)
        {
            state.Children.Add(new TextBlock { Text = detail, FontFamily = Font, FontSize = 11, Foreground = Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var body = new Grid();
        var text = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                state,
                new TextBlock { Text = item.Title, FontFamily = Font, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimaryBrush"), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        body.Children.Add(text);
        if (item.Remove is { } remove)
        {
            // The card's ×, top right: off the home for good (its files stay where they are).
            var close = new Button
            {
                Content = new FontIcon { Glyph = "", FontFamily = new FontFamily(IconFont), FontSize = 8, Foreground = Brush("TextSecondaryBrush") },
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -6, 0),
            };
            AutomationProperties.SetName(close, $"Remove {item.Title}");
            ToolTipService.SetToolTip(close, "Remove from here");
            close.Click += (_, _) =>
            {
                _leave.Stop();
                _ = Task.Run(remove);
            };
            body.Children.Add(close);
        }

        if (item.Second is { } second)
        {
            // A small pill in the card's top corner (Retry), separate from opening the card.
            var extra = new Button
            {
                Content = second.Label,
                Height = 22,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(11),
                FontSize = 11,
                FontFamily = Font,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, item.Remove is null ? -4 : 16, 0), // beside the state line, clear of the title (and the ×)
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            extra.Click += (_, _) =>
            {
                _leave.Stop();
                _ = Task.Run(second.Invoke);
            };
            body.Children.Add(extra);
        }

        var card = new Button
        {
            Width = width,
            Height = 76,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(14),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = Glass,
            BorderBrush = GlassEdge,
            BorderThickness = new Thickness(1),
            Content = body,
        };
        Hover(card);
        AutomationProperties.SetName(card, item.Title);
        // A card at the side comes to the middle first; the middle one opens.
        card.Click += (_, _) =>
        {
            if (card.Tag is int at && at != _focus)
            {
                Focus(at);
            }
            else
            {
                Run(item);
            }
        };
        return card;
    }

    /// <summary>A rounded glass pill with an icon and a short label, for things to start.</summary>
    private Button Pill(HomeItem item)
    {
        var pill = new Button
        {
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(17),
            Background = Glass,
            BorderBrush = GlassEdge,
            BorderThickness = new Thickness(1),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = item.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 13, Foreground = Brush("TextPrimaryBrush") },
                    new TextBlock { Text = item.Title, FontFamily = Font, FontSize = 12, Foreground = Brush("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        Hover(pill);
        AutomationProperties.SetName(pill, item.Title);
        if (item.Detail is { } detail)
        {
            ToolTipService.SetToolTip(pill, detail);
        }

        pill.Click += (_, _) => Run(item);
        return pill;
    }

    private void Run(HomeItem item)
    {
        _leave.Stop();
        _ = Task.Run(item.Invoke);
    }

    /// <summary>Glass that warms toward the reply blue under the pointer.</summary>
    private static void Hover(Button button)
    {
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x33, 0x80, 0xFF));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x47, 0x33, 0x80, 0xFF));
        button.Resources["ButtonBorderBrushPointerOver"] = GlassEdge;
    }

    /// <summary>The same glass as the form cards.</summary>
    private static Brush Glass => NotchFormView.Glass;

    private static Brush GlassEdge => NotchFormView.GlassEdge;

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
