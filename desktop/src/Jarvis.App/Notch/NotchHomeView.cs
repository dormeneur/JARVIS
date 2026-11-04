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
    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _pills = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
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
        _cards.Children.Clear();
        var cardWidth = (Width - (8 * (Math.Max(1, home.Recent.Count) - 1))) / Math.Max(1, Math.Min(3, home.Recent.Count));
        foreach (var item in home.Recent.Take(3))
        {
            _cards.Children.Add(Card(item, Math.Min(cardWidth, 180)));
        }

        _cards.Visibility = home.Recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pills.Children.Clear();
        foreach (var item in home.Actions)
        {
            _pills.Children.Add(Pill(item));
        }

        View.Visibility = Visibility.Visible;
        if (!refresh)
        {
            Leave(NeverEntered);
        }
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
                Margin = new Thickness(0, -3, -4, 0), // beside the state line, clear of the title
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
        card.Click += (_, _) => Run(item);
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
