using Jarvis.App.Settings;
using Jarvis.Contracts.Notch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jarvis.App.Notch;

/// <summary>
/// Settings inside the notch: the same sections and pages as the Settings window (built fresh
/// each time, so they show the current settings), grouped down the side, with Pop out to move to
/// the window on the same page. Escape closes it.
/// </summary>
internal sealed class NotchSettingsView
{
    private const string IconFont = "Segoe Fluent Icons,Segoe MDL2 Assets";

    /// <summary>Where the sidebar's second group starts: the things Jarvis does for you.</summary>
    private const string FirstWorkSection = "Agents";

    private readonly IServiceProvider _services;
    private readonly IReadOnlyList<SettingsSection> _sections = new SettingsViewModel().Sections;
    private readonly StackPanel _menu = new() { Spacing = 2 };
    private readonly Dictionary<string, Button> _items = [];
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private NotchState.Settings? _settings;
    private string? _section;

    public NotchSettingsView(Grid area, IServiceProvider services)
    {
        _services = services;
        var font = Font;
        _menu.Children.Add(new TextBlock { Text = "Settings", FontFamily = font, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimaryBrush"), Margin = new Thickness(10, 0, 0, 10) });
        _menu.Children.Add(GroupLabel("JARVIS"));
        foreach (var section in _sections)
        {
            if (section.Title == FirstWorkSection)
            {
                _menu.Children.Add(GroupLabel("WORK"));
            }

            var item = MenuItem(section);
            _items[section.Title] = item;
            _menu.Children.Add(item);
        }

        var popOut = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = NotchFormView.Glass,
            BorderBrush = NotchFormView.GlassEdge,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 40, 0), // left of the panel's ×
            Content = new FontIcon { Glyph = "", FontFamily = new FontFamily(IconFont), FontSize = 13, Foreground = Brush("TextPrimaryBrush") },
        };
        AutomationProperties.SetName(popOut, "Open in a window");
        ToolTipService.SetToolTip(popOut, "Open in a window");
        popOut.Click += (_, _) => _settings?.PopOut(_section);

        var right = new Grid { RowSpacing = 0 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(popOut);
        Grid.SetRow(_page, 1);
        right.Children.Add(_page);

        var sidebar = new ScrollViewer { Content = _menu, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) };
        View = new Grid
        {
            ColumnSpacing = 6,
            Margin = new Thickness(14, 12, 6, 12),
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        View.Children.Add(sidebar);
        Grid.SetColumn(right, 1);
        View.Children.Add(right);
        View.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                _settings?.Close();
            }
        };
        area.Children.Add(View);
    }

    public Grid View { get; }

    private static FontFamily Font => (FontFamily)Application.Current.Resources["FontText"];

    public void Show(NotchState.Settings settings)
    {
        View.Visibility = Visibility.Visible;
        if (ReferenceEquals(settings, _settings))
        {
            return;
        }

        _settings = settings;
        Open(settings.Section is { } wanted && _items.ContainsKey(wanted) ? wanted : _sections[0].Title);
    }

    public void Hide()
    {
        View.Visibility = Visibility.Collapsed;
        _page.Content = null; // pages hold view models; a fresh one next time
        _settings = null;
    }

    private void Open(string title)
    {
        _section = title;
        foreach (var (name, item) in _items)
        {
            item.Background = name == title ? NotchFormView.Glass : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            item.BorderThickness = new Thickness(name == title ? 1 : 0);
        }

        var section = _sections.First(candidate => candidate.Title == title);
        _page.Content = ActivatorUtilities.CreateInstance(_services, section.Page);
    }

    private Button MenuItem(SettingsSection section)
    {
        var item = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = NotchFormView.GlassEdge,
            BorderThickness = new Thickness(0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new FontIcon { Glyph = section.Glyph, FontFamily = new FontFamily(IconFont), FontSize = 13, Foreground = Brush("TextSecondaryBrush") },
                    new TextBlock { Text = section.Title, FontFamily = Font, FontSize = 13, Foreground = Brush("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        item.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        item.Click += (_, _) => Open(section.Title);
        return item;
    }

    private static TextBlock GroupLabel(string text) => new()
    {
        Text = text,
        FontFamily = Font,
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        CharacterSpacing = 60,
        Foreground = Brush("TextTertiaryBrush"),
        Margin = new Thickness(10, 8, 0, 4),
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
