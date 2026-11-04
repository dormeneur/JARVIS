using System.Globalization;
using Jarvis.Contracts.Calendar;
using Jarvis.Contracts.Notch;
using Jarvis.Core.Notch;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Jarvis.App.Notch;

/// <summary>
/// An event Jarvis is about to add, laid out for checking: title, day, start and end (or all day),
/// place and notes, every one editable in place. Add to calendar takes what the fields say;
/// Cancel adds nothing. A day or time it can't read is said under the fields, and nothing is added.
/// </summary>
internal sealed class NotchEventView
{
    private readonly NotchWindow _window;
    private readonly TextBlock _destination;
    private readonly TextBox _title;
    private readonly TextBox _day;
    private readonly TextBox _start;
    private readonly TextBox _end;
    private readonly CheckBox _allDay;
    private readonly TextBox _location;
    private readonly TextBox _notes;
    private readonly TextBlock _problem;
    private NotchState.EventDraft? _draft;
    private bool _typing;

    public NotchEventView(NotchWindow window, Grid area)
    {
        _window = window;
        var font = Font;
        _destination = new TextBlock { FontFamily = font, FontSize = 11, Foreground = Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        _title = Field("Title");
        _title.FontSize = 15;
        _title.FontWeight = FontWeights.SemiBold;
        _day = Field("Day, e.g. Mon 26 Oct 2026");
        _start = Field("Start");
        _end = Field("End");
        _allDay = new CheckBox
        {
            Content = new TextBlock { Text = "All day", FontFamily = font, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
            MinWidth = 0,
            Padding = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _allDay.Checked += (_, _) => ShowTimes();
        _allDay.Unchecked += (_, _) => ShowTimes();
        _location = Field("Place (optional)");
        _notes = Field("Notes (optional)");
        _problem = new TextBlock { FontFamily = font, FontSize = 11, Foreground = Brush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var when = new Grid { ColumnSpacing = 8 };
        foreach (var width in new[] { 2.2, 1, 1, 0 })
        {
            when.ColumnDefinitions.Add(new ColumnDefinition { Width = width > 0 ? new GridLength(width, GridUnitType.Star) : GridLength.Auto });
        }

        foreach (var (control, column) in new (FrameworkElement, int)[] { (Card(_day), 0), (Card(_start), 1), (Card(_end), 2), (_allDay, 3) })
        {
            Grid.SetColumn(control, column);
            when.Children.Add(control);
        }

        var add = new Button { Content = "Add to calendar", Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 0, 14, 0), FontSize = 12, FontFamily = font, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        add.Click += (_, _) => Add();
        var cancel = new Button { Content = "Cancel", Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 0, 14, 0), FontSize = 12, FontFamily = font };
        cancel.Click += (_, _) =>
        {
            StopTyping();
            _draft?.Cancel();
        };

        View = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 8, NotchLayout.ContentHorizontalPadding, 10),
            Visibility = Visibility.Collapsed,
            Children =
            {
                new TextBlock { Text = "Add to your calendar?", FontFamily = font, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimaryBrush") },
                _destination,
                Card(_title),
                when,
                Card(_location),
                Card(_notes),
                _problem,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, add } },
            },
        };
        area.Children.Add(View);
    }

    public StackPanel View { get; }

    private static FontFamily Font => (FontFamily)Application.Current.Resources["FontText"];

    public void Show(NotchState.EventDraft draft)
    {
        View.Visibility = Visibility.Visible;
        if (ReferenceEquals(draft, _draft))
        {
            return;
        }

        _draft = draft;
        var e = draft.Event;
        _destination.Text = $"Goes to {draft.Destination}. Check it, change anything, then add it.";
        _title.Text = e.Title;
        _day.Text = e.Start.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
        _start.Text = e.Start.ToString("h:mm tt", CultureInfo.InvariantCulture);
        _end.Text = e.End.ToString("h:mm tt", CultureInfo.InvariantCulture);
        _allDay.IsChecked = e.AllDay;
        _location.Text = e.Location;
        _notes.Text = e.Notes;
        _problem.Visibility = Visibility.Collapsed;
        ShowTimes();
    }

    public void Hide()
    {
        StopTyping();
        View.Visibility = Visibility.Collapsed;
        _draft = null;
    }

    private void Add()
    {
        if (_draft is not { } draft)
        {
            return;
        }

        if (Read(draft.Event) is not { } edited)
        {
            _problem.Text = "I can't read that day or time. Try \"Mon 26 Oct 2026\" and \"1:30 PM\".";
            _problem.Visibility = Visibility.Visible;
            return;
        }

        StopTyping();
        draft.Add(edited);
    }

    /// <summary>The event as the fields now say, or null when the day or a time can't be read.</summary>
    private CalendarEvent? Read(CalendarEvent original)
    {
        string[] days = ["ddd d MMM yyyy", "d MMM yyyy", "ddd d MMMM yyyy", "d MMMM yyyy", "yyyy-MM-dd", "d/M/yyyy"];
        if (!DateTime.TryParseExact(_day.Text.Trim(), days, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var day))
        {
            return null;
        }

        var offset = original.Start.Offset;
        var title = _title.Text.Trim().Length > 0 ? _title.Text.Trim() : original.Title;
        if (_allDay.IsChecked == true)
        {
            var date = new DateTimeOffset(day.Date, offset);
            return original with { Title = title, Start = date, End = date.AddDays(1), AllDay = true, Location = _location.Text.Trim(), Notes = _notes.Text.Trim() };
        }

        string[] times = ["h:mm tt", "h tt", "htt", "h:mmtt", "H:mm", "HH:mm"];
        if (!DateTime.TryParseExact(_start.Text.Trim().ToUpperInvariant(), times, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var start)
            || !DateTime.TryParseExact(_end.Text.Trim().ToUpperInvariant(), times, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var end))
        {
            return null;
        }

        var from = new DateTimeOffset(day.Date + start.TimeOfDay, offset);
        var to = new DateTimeOffset(day.Date + end.TimeOfDay, offset);
        return original with { Title = title, Start = from, End = to > from ? to : to.AddDays(1), AllDay = false, Location = _location.Text.Trim(), Notes = _notes.Text.Trim() };
    }

    private void ShowTimes()
    {
        var timed = _allDay.IsChecked != true;
        _start.IsEnabled = timed;
        _end.IsEnabled = timed;
        _start.Opacity = _end.Opacity = timed ? 1 : 0.4;
    }

    private TextBox Field(string placeholder)
    {
        var box = new TextBox
        {
            FontFamily = Font,
            FontSize = 13,
            Foreground = Brush("TextPrimaryBrush"),
            PlaceholderText = placeholder,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 2, 0, 2),
            MinHeight = 0,
        };

        // The glass card is the field: no box of its own, as on the form cards.
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
        {
            box.Resources[key] = clear;
        }

        box.Resources["TextControlForegroundFocused"] = box.Foreground;
        box.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => StartTyping(box)), handledEventsToo: true);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Add();
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                StopTyping();
            }
        };
        return box;
    }

    private static Border Card(FrameworkElement content) => NotchFormView.GlassPanel(content, 10, new Thickness(10, 4, 10, 4));

    private void StartTyping(TextBox box)
    {
        if (!_typing)
        {
            _typing = true;
            _window.BeginTyping();
        }

        box.Focus(FocusState.Pointer);
    }

    private void StopTyping()
    {
        if (_typing)
        {
            _typing = false;
            _window.EndTyping();
        }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
