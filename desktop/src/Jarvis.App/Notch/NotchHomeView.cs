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
/// The notch's home: a greeting, a row of glass tiles (things to start) and recent work. The mouse
/// leaving it for a moment closes it, as does never coming into it.
/// </summary>
internal sealed class NotchHomeView
{
    private const string IconFont = "Segoe Fluent Icons,Segoe MDL2 Assets";
    private const double Width = NotchLayout.ExpandedWidth - (2 * NotchLayout.ShoulderRadius) - (2 * NotchLayout.ContentHorizontalPadding);
    /// <summary>It holds this long after the pointer leaves (DESIGN-FACTS.md §3, notch hover dwell).</summary>
    private static readonly TimeSpan LeaveGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NeverEntered = TimeSpan.FromSeconds(8);

    private readonly TextBlock _greeting;
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _recent = new() { Spacing = 4 };
    private readonly TextBlock _recentTitle;
    private readonly DispatcherQueueTimer _leave;
    private NotchState.Home? _home;
    private bool _entered;

    public NotchHomeView(Grid area)
    {
        var font = Font;
        _greeting = new TextBlock { FontFamily = font, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimaryBrush") };
        _recentTitle = new TextBlock { Text = "Lately", FontFamily = font, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextSecondaryBrush"), Margin = new Thickness(0, 4, 0, 0) };
        View = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 8, NotchLayout.ContentHorizontalPadding, 10),
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), // so pointer events reach it everywhere
            Children = { _greeting, _tiles, _recentTitle, _recent },
        };
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

        _home = home;
        _entered = false;
        _greeting.Text = home.Greeting;
        _tiles.Children.Clear();
        var width = (Width - (8 * (home.Actions.Count - 1))) / Math.Max(1, home.Actions.Count);
        foreach (var item in home.Actions)
        {
            _tiles.Children.Add(Tile(item, width));
        }

        _recent.Children.Clear();
        foreach (var item in home.Recent)
        {
            _recent.Children.Add(Row(item));
        }

        _recentTitle.Visibility = home.Recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        View.Visibility = Visibility.Visible;
        Leave(NeverEntered);
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

    private Button Tile(HomeItem item, double width)
    {
        var tile = new Button
        {
            Width = width,
            Height = 64,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(12),
            Background = Glass,
            BorderBrush = GlassEdge,
            BorderThickness = new Thickness(1),
            Content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = item.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 16, Foreground = Brush("TextPrimaryBrush") },
                    new TextBlock { Text = item.Title, FontFamily = Font, FontSize = 11, Foreground = Brush("TextSecondaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        Hover(tile);
        AutomationProperties.SetName(tile, item.Title);
        if (item.Detail is { } detail)
        {
            ToolTipService.SetToolTip(tile, detail);
        }

        tile.Click += (_, _) => Run(item);
        return tile;
    }

    private Button Row(HomeItem item)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = item.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 12, Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = item.Title, FontFamily = Font, FontSize = 12, Foreground = Brush("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        if (item.Detail is { } detail)
        {
            var meta = new TextBlock { Text = detail, FontFamily = Font, FontSize = 11, Foreground = Brush("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(meta, 2);
            row.Children.Add(meta);
        }

        var button = new Button
        {
            Width = Width,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Glass,
            BorderBrush = GlassEdge,
            BorderThickness = new Thickness(1),
            Content = row,
        };
        Hover(button);
        AutomationProperties.SetName(button, item.Title);
        button.Click += (_, _) => Run(item);
        return button;
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
