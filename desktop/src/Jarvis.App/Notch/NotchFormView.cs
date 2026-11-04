using System.Globalization;
using System.Numerics;
using Jarvis.Contracts.Notch;
using Jarvis.Core.Notch;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Jarvis.App.Notch;

/// <summary>
/// The notch's form-filling panel, two pages side by side that swipe like a carousel (touchpad,
/// snap scrolling) or switch with the folder icon:
/// - Answers: a status line, a thin progress bar while Jarvis fills the page, one card per
///   question and a bar that saves typed answers after the user confirms. One button on top does
///   the next thing: Fill it in, Stop, Update answers, then Done.
/// - Files: the resume, this form's context files (pick or drop them) and past forms, each deletable.
/// A card the user has typed in keeps their text when more answers arrive.
/// </summary>
internal sealed class NotchFormView
{
    private const double PageWidth = NotchLayout.ExpandedWidth - (2 * NotchLayout.ShoulderRadius) - (2 * NotchLayout.ContentHorizontalPadding);
    private const string IconFont = "Segoe Fluent Icons,Segoe MDL2 Assets";
    private const string CopyGlyph = "";
    private const string MicGlyph = "";
    private const string StopGlyph = "";
    private const string DictionaryGlyph = "";
    private const string SettingsGlyph = "";
    private const string FolderGlyph = "";
    private const string BackGlyph = "";
    private const string DeleteGlyph = "";
    private const string UploadGlyph = "";
    private const string AttachGlyph = "";
    private const string SourceGlyph = "";
    private const string FilledGlyph = "";

    private const string ReloadGlyph = "\uE72C";

    /// <summary>The bar glides to each new value (DESIGN-FACTS.md §1.2 Content spring's response).</summary>
    private static readonly TimeSpan ProgressGlide = TimeSpan.FromMilliseconds(340);

    private readonly NotchWindow _window;
    private readonly Action _clickable;
    private readonly Action _openSettings;
    private readonly DispatcherQueue _dispatcher;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly Grid _progress;
    private readonly Border _progressFill;
    private readonly Button _pageButton;
    private readonly Button _primary;
    private readonly ScrollViewer _carousel;
    private readonly Ellipse[] _dots;
    private readonly StackPanel _cards = new() { Spacing = 8 };
    private readonly StackPanel _bar;
    private readonly StackPanel _files = new() { Spacing = 6 };
    private readonly TextBlock _filesNote;
    private readonly Dictionary<string, Card> _shown = [];
    private NotchState.Form? _form;
    private bool _typing;
    private bool _confirming;
    private string? _savedNote;
    private int _page;

    /// <param name="clickable">Makes the panel take clicks again (after typing ends).</param>
    /// <param name="openSettings">Opens Settings on the Vault page.</param>
    public NotchFormView(NotchWindow window, Grid area, Action clickable, Action openSettings)
    {
        _window = window;
        _clickable = clickable;
        _openSettings = openSettings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        var font = Font;
        _title = new TextBlock { FontFamily = font, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        _status = new TextBlock { FontFamily = font, FontSize = 12, Foreground = Brush("TextSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap };

        _progressFill = new Border { Background = Accent, CornerRadius = new CornerRadius(1.5), Scale = new Vector3(0, 1, 1), ScaleTransition = new Vector3Transition { Duration = ProgressGlide } };
        _progress = new Grid { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Brush("BorderSubtleBrush"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), Children = { _progressFill } };

        _pageButton = IconButton(FolderGlyph, "Files for this form", () => GoTo(_page == 0 ? 1 : 0));
        _primary = Pill("Fill it in", accent: true, OnPrimary);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { IconButton(ReloadGlyph, "Answer everything again with your latest files", () => Actions?.Reload?.Invoke(null)), _pageButton, IconButton(SettingsGlyph, "Vault settings", openSettings), _primary, IconButton("\uE711", "Close (Esc)", () => Actions?.Dismiss()) },
        };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new StackPanel { Spacing = 2, Children = { _title, _status, _progress } });
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

        // Page 1: the answers.
        _bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var answers = new Grid { Width = PageWidth, RowSpacing = 6 };
        answers.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        answers.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        answers.Children.Add(new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(_bar, 1);
        answers.Children.Add(_bar);

        // Page 2: files. Dropping files anywhere on it adds them as context for this form.
        _filesNote = new TextBlock { FontFamily = font, FontSize = 12, Foreground = Brush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
        var filesPage = new ScrollViewer { Width = PageWidth, Content = _files, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        filesPage.DragOver += (_, e) => e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        filesPage.Drop += async (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var dropped = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().Select(file => file.Path).ToList();
                await AddContextAsync(dropped).ConfigureAwait(true);
            }
        };

        _carousel = new ScrollViewer
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { answers, filesPage } },
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalSnapPointsType = SnapPointsType.MandatorySingle,
            HorizontalSnapPointsAlignment = SnapPointsAlignment.Near,
            IsHorizontalRailEnabled = true,
        };
        _carousel.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
            {
                SetPage((int)Math.Round(_carousel.HorizontalOffset / PageWidth));
            }
        };

        _dots = [Dot(), Dot()];
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, Children = { _dots[0], _dots[1] } };
        for (var i = 0; i < _dots.Length; i++)
        {
            var page = i;
            _dots[i].Tapped += (_, _) => GoTo(page);
        }

        View = new Grid
        {
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 8, NotchLayout.ContentHorizontalPadding, 8),
            RowSpacing = 6,
            Visibility = Visibility.Collapsed,
        };
        View.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        View.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        View.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_carousel, 1);
        Grid.SetRow(dots, 2);
        View.Children.Add(header);
        View.Children.Add(_carousel);
        View.Children.Add(dots);
        area.Children.Add(View);
        SetPage(0);
    }

    public Grid View { get; }

    private static FontFamily Font => (FontFamily)Application.Current.Resources["FontText"];

    private static Brush Accent => new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["AccentBlueColor"]);

    private static Brush Warning => new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["WarningColor"]);

    private static Brush Success => new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SuccessColor"]);

    private FormActions? Actions => _form?.Actions;

    public void Show(NotchState.Form form)
    {
        var same = _form is not null && _form.Title == form.Title && _form.Cards.Select(card => card.Id).SequenceEqual(form.Cards.Select(card => card.Id));
        _form = form;
        _title.Text = form.Title.Length > 0 ? form.Title : "Form";
        _status.Text = _savedNote ?? form.Status;
        if (!same)
        {
            _shown.Clear();
            _cards.Children.Clear();
            _confirming = false;
            _savedNote = null;
            _status.Text = form.Status;
            GoTo(0, animate: false);
            foreach (var card in form.Cards)
            {
                var ui = new Card(this, card);
                _shown[card.Id] = ui;
                _cards.Children.Add(ui.Root);
            }
        }

        foreach (var card in form.Cards)
        {
            _shown[card.Id].Update(card);
        }

        ShowProgress(form.FillProgress);
        ShowPrimary();
        View.Visibility = Visibility.Visible;
        ShowBar();
    }

    public void Hide()
    {
        View.Visibility = Visibility.Collapsed;
        _form = null;
        StopTyping();
    }

    /// <summary>The window lost focus (the user clicked back into the page): stop typing, keep the panel.</summary>
    public void Deactivated() => StopTyping();

    /// <summary>The bar glides from where it is; it shows only while Jarvis is filling.</summary>
    private void ShowProgress(double? progress)
    {
        _progress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        _progressFill.CenterPoint = Vector3.Zero;
        _progressFill.Scale = new Vector3((float)(progress ?? 0), 1, 1);
    }

    /// <summary>
    /// The one button on top: Fill it in (greyed until an answer is ready), Stop while filling,
    /// Update answers once something changed after filling, Try again when some didn't go in, and
    /// Done when the page has it all.
    /// </summary>
    private void ShowPrimary()
    {
        if (_form is not { } form)
        {
            return;
        }

        var anyFilled = form.Cards.Any(card => card.Filled);
        var typed = _shown.Values.Any(card => card.Typed);
        (string Label, bool Enabled, bool Accent) look = form.Actions.StopFilling is not null ? ("Stop", true, false)
            : form.Fillable > 0 || typed ? (anyFilled ? "Update answers" : "Fill it in", true, true)
            : form.Retry > 0 ? ("Try again", true, true)
            : anyFilled ? ("Done", true, true)
            : ("Fill it in", false, true);
        _primary.Content = look.Label;
        _primary.IsEnabled = look.Enabled;
        _primary.Style = look.Accent ? (Style)Application.Current.Resources["AccentButtonStyle"] : null;
    }

    private void OnPrimary()
    {
        if (Actions is not { } actions)
        {
            return;
        }

        if (actions.StopFilling is { } stop)
        {
            stop();
            return;
        }

        StopTyping(); // real keystrokes must go to the page, not to a card
        var typed = _shown.Where(pair => pair.Value.Typed).ToList();
        if (_form!.Fillable == 0 && typed.Count == 0 && _form.Retry == 0)
        {
            actions.Dismiss(); // Done
            return;
        }

        foreach (var (id, card) in typed)
        {
            actions.Used?.Invoke(id, card.Text); // what the user typed is what goes in
        }

        actions.FillIn?.Invoke();
    }

    private void GoTo(int page, bool animate = true)
    {
        _carousel.ChangeView(page * PageWidth, null, null, disableAnimation: !animate);
        SetPage(page);
    }

    private void SetPage(int page)
    {
        page = Math.Clamp(page, 0, _dots.Length - 1);
        var changed = page != _page;
        _page = page;
        for (var i = 0; i < _dots.Length; i++)
        {
            _dots[i].Opacity = i == page ? 0.9 : 0.3;
        }

        ((FontIcon)_pageButton.Content).Glyph = page == 0 ? FolderGlyph : BackGlyph;
        ToolTipService.SetToolTip(_pageButton, page == 0 ? "Files for this form" : "Back to the answers");
        if (changed && page == 1)
        {
            _ = LoadFilesAsync(null);
        }
    }

    private async Task LoadFilesAsync(string? note)
    {
        if (Actions?.Files is not { } files)
        {
            return;
        }

        _filesNote.Text = note ?? "Loading…";
        BuildFiles(null, files);
        try
        {
            var library = await Task.Run(files.Load).ConfigureAwait(true);
            _filesNote.Text = note ?? "Drop files here, or add them, to give Jarvis more to go on for this form.";
            BuildFiles(library, files);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _filesNote.Text = $"Couldn't load your files: {exception.Message}";
        }
    }

    private void BuildFiles(FormLibrary? library, FormFiles files)
    {
        _files.Children.Clear();
        _files.Children.Add(_filesNote);
        _files.Children.Add(Section("Resume", IconPill(UploadGlyph, "Upload latest", () => _ = PickResumeAsync(files))));
        foreach (var file in library?.Resumes ?? [])
        {
            _files.Children.Add(FileRow(file, files, file == library!.Resumes[0] ? "latest" : null));
        }

        _files.Children.Add(Section("Context for this form", IconPill(AttachGlyph, "Add files", () => _ = PickContextAsync())));
        foreach (var file in library?.Context ?? [])
        {
            _files.Children.Add(FileRow(file, files, null));
        }

        _files.Children.Add(Section("Past forms", null));
        foreach (var file in library?.History ?? [])
        {
            _files.Children.Add(FileRow(file, files, file.Name == _title.Text ? "this one" : null));
        }
    }

    private Grid Section(string title, Button? action)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = title, FontFamily = Font, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
        }

        return row;
    }

    /// <summary>A stored file with its date, and a bin that asks once more before deleting.</summary>
    private Border FileRow(StoredFile file, FormFiles files, string? tag)
    {
        var when = file.Modified is { } at ? at.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture) : string.Empty;
        var name = new TextBlock { Text = file.Name, FontFamily = Font, FontSize = 12, Foreground = Brush("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var meta = new TextBlock { Text = tag is null ? when : $"{when} · {tag}", FontFamily = Font, FontSize = 11, Foreground = Brush("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        Button? bin = null;
        bin = IconButton(DeleteGlyph, $"Delete {file.Name}", () =>
        {
            actions.Children.Clear();
            actions.Children.Add(Pill("Delete?", accent: true, () => _ = DeleteAsync(file, files)));
            var timer = _dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                actions.Children.Clear();
                actions.Children.Add(bin!);
            };
            timer.Start();
        });
        actions.Children.Add(bin);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(name);
        Grid.SetColumn(meta, 1);
        row.Children.Add(meta);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);
        return GlassPanel(row, 8, new Thickness(10, 4, 4, 4));
    }

    private async Task DeleteAsync(StoredFile file, FormFiles files)
        => await LoadFilesAsync(await Task.Run(() => files.Delete(file.Path)).ConfigureAwait(true)).ConfigureAwait(true);

    private async Task PickResumeAsync(FormFiles files)
    {
        var picker = Picker(".pdf", ".docx", ".md", ".txt");
        if (await picker.PickSingleFileAsync() is { } file)
        {
            _filesNote.Text = "Saving your resume to the vault…";
            await LoadFilesAsync(await Task.Run(() => files.AddResume(file.Path)).ConfigureAwait(true)).ConfigureAwait(true);
        }
    }

    private async Task PickContextAsync()
    {
        var picked = await Picker("*").PickMultipleFilesAsync();
        await AddContextAsync([.. picked.Select(file => file.Path)]).ConfigureAwait(true);
    }

    private async Task AddContextAsync(List<string> paths)
    {
        if (paths.Count == 0 || Actions?.Files is not { } files)
        {
            return;
        }

        _filesNote.Text = "Adding to your vault…";
        await LoadFilesAsync(await Task.Run(() => files.AddContext(paths)).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private FileOpenPicker Picker(params string[] types)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var type in types)
        {
            picker.FileTypeFilter.Add(type);
        }

        WinRT.Interop.InitializeWithWindow.Initialize(picker, _window.Handle);
        return picker;
    }

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
            _clickable();
        }
    }

    private List<FormEdit> Edits()
        => [.. _form?.Cards.Select(card => _shown[card.Id]).Where(ui => ui.Edited).Select(ui => new FormEdit(ui.Question, ui.Text)) ?? []];

    private void ShowBar()
    {
        _bar.Children.Clear();
        var edits = Edits();
        if (edits.Count == 0 || _form is not { } form)
        {
            _confirming = false;
            _bar.Visibility = Visibility.Collapsed;
            return;
        }

        _bar.Visibility = Visibility.Visible;
        var count = edits.Count == 1 ? "1 answer" : $"{edits.Count} answers";
        if (!_confirming)
        {
            _bar.Children.Add(Pill($"Save {count} to your vault", accent: false, () =>
            {
                _confirming = true;
                ShowBar();
            }));
            return;
        }

        _bar.Children.Add(new TextBlock { Text = $"Keep {count} in your standard answers?", FontFamily = Font, FontSize = 12, Foreground = Brush("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        _bar.Children.Add(Pill("Save", accent: true, () => _ = SaveAsync(form, edits)));
        _bar.Children.Add(Pill("Cancel", accent: false, () =>
        {
            _confirming = false;
            ShowBar();
        }));
    }

    private async Task SaveAsync(NotchState.Form form, List<FormEdit> edits)
    {
        _confirming = false;
        _bar.Children.Clear();
        _status.Text = "Saving to your vault…";
        var said = await Task.Run(() => form.Actions.Save(edits)).ConfigureAwait(true);
        if (_form is null)
        {
            return; // dismissed meanwhile
        }

        _savedNote = said;
        _status.Text = said;
        if (said.StartsWith("Saved", StringComparison.Ordinal))
        {
            foreach (var ui in _shown.Values.Where(ui => ui.Edited))
            {
                ui.MarkSaved();
            }
        }

        ShowBar();
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(4);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _savedNote = null;
            if (_form is not null)
            {
                _status.Text = _form.Status;
            }
        };
        timer.Start();
    }

    private static Ellipse Dot() => new() { Width = 5, Height = 5, Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Opacity = 0.3 };

    private static Button Pill(string label, bool accent, Action click, double height = 24)
    {
        var button = new Button
        {
            Content = label,
            Height = height,
            MinWidth = height == 24 ? 56 : 0,
            Padding = new Thickness(height == 24 ? 12 : 9, 0, height == 24 ? 12 : 9, 0),
            CornerRadius = new CornerRadius(height / 2),
            FontSize = height == 24 ? 12 : 11,
            FontFamily = Font,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (accent)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }

        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A round icon-only button, named for screen readers and with its name as a tooltip.</summary>
    private static Button IconButton(string glyph, string name, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFont), FontSize = 12 },
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private static Button IconPill(string glyph, string label, Action click)
    {
        var button = Pill(label, accent: false, click, height: 22);
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                new FontIcon { Glyph = glyph, FontFamily = new FontFamily(IconFont), FontSize = 11 },
                new TextBlock { Text = label, FontFamily = Font, FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        return button;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// Frosted glass over the notch's black: a faint lift of the reply glow's blue (0x3380FF,
    /// DESIGN-FACTS.md §2.3) fading to a whisper of white, under a hairline that catches light
    /// at the top. Our own treatment: the spec has no cards.
    /// </summary>
    internal static Brush Glass => Gradient(Windows.UI.Color.FromArgb(0xC8, 0x10, 0x16, 0x2A), Windows.UI.Color.FromArgb(0xB4, 0x0A, 0x0D, 0x18), vertical: false);

    internal static Brush GlassEdge => Gradient(Windows.UI.Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF), Windows.UI.Color.FromArgb(0x1A, 0x40, 0xBF, 0xFF), vertical: true);

    /// <summary>Light caught in thick glass: a bright band along the top edge that bends away fast.</summary>
    internal static Brush Sheen => new LinearGradientBrush
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(0, 1),
        GradientStops =
        {
            new GradientStop { Color = Windows.UI.Color.FromArgb(0x1F, 0xB3, 0xE6, 0xFF), Offset = 0 },
            new GradientStop { Color = Windows.UI.Color.FromArgb(0x0A, 0x40, 0xBF, 0xFF), Offset = 0.35 },
            new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00), Offset = 1 },
        },
    };

    /// <summary>A glass panel: the dense body, then the sheen over it, then the content on top.</summary>
    internal static Border GlassPanel(UIElement content, double radius, Thickness padding) => new()
    {
        Background = Glass,
        BorderBrush = GlassEdge,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Child = new Grid
        {
            Children =
            {
                new Border { Background = Sheen, CornerRadius = new CornerRadius(radius - 1, radius - 1, 0, 0), Height = 28, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false },
                new Border { Padding = padding, Child = content },
            },
        },
    };

    private static Brush Chip => new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    private static LinearGradientBrush Gradient(Windows.UI.Color from, Windows.UI.Color to, bool vertical = false) => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = vertical ? new Windows.Foundation.Point(0, 1) : new Windows.Foundation.Point(1, 1),
        GradientStops = { new GradientStop { Color = from, Offset = 0 }, new GradientStop { Color = to, Offset = 1 } },
    };

    /// <summary>A translucent pill for picking an answer.</summary>
    private static Button ChipButton(string label, Action click)
    {
        var chip = Pill(label, accent: false, click, height: 22);
        chip.Background = Chip;
        chip.BorderBrush = GlassEdge;
        chip.BorderThickness = new Thickness(1);
        chip.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0x33, 0x80, 0xFF));
        chip.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x33, 0x80, 0xFF));
        return chip;
    }

    /// <summary>One question's card.</summary>
    private sealed class Card
    {
        private readonly NotchFormView _owner;
        private readonly string _id;
        private readonly TextBox _answer;
        private readonly TextBlock _meta;
        private readonly FontIcon _source;
        private readonly FontIcon _filled;
        private readonly ProgressRing _working;
        private readonly Button _copy;
        private readonly Button _mic;
        private readonly Button _dictionary;
        private readonly Button _reload;
        private readonly StackPanel _choices = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        private readonly ScrollViewer _choiceRow;
        private IReadOnlyList<string> _shownChoices = [];
        private string _original = string.Empty;
        private string? _given;
        private TaskCompletionSource? _listening;
        private bool _setting;

        public Card(NotchFormView owner, FormCard card)
        {
            _owner = owner;
            _id = card.Id;
            Question = card.Question;
            var font = Font;
            _answer = new TextBox
            {
                FontFamily = font,
                FontSize = 13,
                Foreground = Brush("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 2, 0, 2),
                MinHeight = 0,
                MaxHeight = 120,
                PlaceholderText = "Type or say your answer",
            };

            // The card is the field: no box of its own, as in the composer.
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
            {
                _answer.Resources[key] = clear;
            }

            _answer.Resources["TextControlForegroundFocused"] = _answer.Foreground;
            _answer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => owner.StartTyping(_answer)), handledEventsToo: true);
            _answer.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    e.Handled = true;
                    owner.StopTyping();
                }
            };
            _answer.TextChanged += (_, _) =>
            {
                ShowNewWords();
                if (!_setting)
                {
                    ShowChoices(_shownChoices);
                    owner.ShowBar();
                    owner.ShowPrimary();
                }
            };

            _choiceRow = new ScrollViewer
            {
                Content = _choices,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollMode = ScrollMode.Enabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 2, 0, 2),
            };

            _working = new ProgressRing { Width = 12, Height = 12, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            _source = new FontIcon { Glyph = SourceGlyph, FontFamily = new FontFamily(IconFont), FontSize = 11, Foreground = Brush("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            _filled = new FontIcon { Glyph = FilledGlyph, FontFamily = new FontFamily(IconFont), FontSize = 11, Foreground = Success, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            ToolTipService.SetToolTip(_filled, "In the page");
            _meta = new TextBlock { FontFamily = font, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            _mic = IconButton(MicGlyph, "Say it (heard on this PC)", () => _ = HearAsync());
            _dictionary = IconButton(DictionaryGlyph, "Add new words to your dictionary", AddToDictionary);
            _reload = IconButton(ReloadGlyph, "Answer this again with your latest files", () => _owner.Actions?.Reload?.Invoke(_id));
            _copy = IconButton(CopyGlyph, "Copy", Copy);
            var signs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { _working, _filled, _source } };
            var footer = new Grid { ColumnSpacing = 6 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(signs);
            Grid.SetColumn(_meta, 1);
            footer.Children.Add(_meta);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _reload, _mic, _dictionary, _copy } };
            Grid.SetColumn(tools, 2);
            footer.Children.Add(tools);

            Root = GlassPanel(
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = card.Question, FontFamily = font, FontSize = 12, Foreground = Brush("TextSecondaryBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 4, 0) },
                        _answer,
                        _choiceRow,
                        footer,
                    },
                },
                10,
                new Thickness(12, 8, 6, 6));
            Root.Width = PageWidth;
        }

        public Border Root { get; }

        public string Question { get; }

        public string Text => _answer.Text.Trim();

        /// <summary>The user typed something Jarvis doesn't have yet (it goes in with Fill it in / Update answers).</summary>
        public bool Typed => Text.Length > 0 && Text != (_given ?? string.Empty).Trim();

        /// <summary>Differs from what the vault said: worth offering to save.</summary>
        public bool Edited => Text.Length > 0 && Text != _original.Trim();

        public void Update(FormCard card)
        {
            var typed = Typed;
            _given = card.Answer;
            if (!typed && _answer.Text != (card.Answer ?? string.Empty)) // never overwrite what the user typed
            {
                SetText(card.Answer ?? string.Empty);
                _original = card.Answer ?? string.Empty;
            }

            var working = card.State is FormCardState.Pending or FormCardState.Working;
            _working.IsActive = working;
            _working.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
            _filled.Visibility = card.Filled ? Visibility.Visible : Visibility.Collapsed;
            // Only a source worth knowing: a vault file or a draft, not "your past forms" every time.
            var newSource = card.Source is { } from && from != Jarvis.Core.Vault.FormFiller.FromMemory && from != Jarvis.Core.Vault.FormFiller.FromYou;
            _source.Visibility = newSource && !working ? Visibility.Visible : Visibility.Collapsed;
            _reload.IsEnabled = !working;
            ToolTipService.SetToolTip(_source, card.Source is null ? null : $"From {card.Source}");
            _mic.Visibility = _owner.Actions?.Hear is null ? Visibility.Collapsed : Visibility.Visible;
            if (_listening is null)
            {
                // Words only when there's something to say; where an answer came from is the icon's tooltip.
                (_meta.Text, _meta.Foreground) = card switch
                {
                    { Note: { } note } => (note, card.State == FormCardState.Unknown ? Warning : Brush("TextTertiaryBrush")),
                    { State: FormCardState.Pending } => ("Looking in your vault…", Brush("TextTertiaryBrush")),
                    { State: FormCardState.Unknown } when Text.Length == 0 => ("Not in your vault", Warning),
                    _ => (string.Empty, Brush("TextTertiaryBrush")),
                };
            }

            ShowChoices(card.Choices ?? []);
        }

        /// <summary>The dictionary button offers only words it doesn't know yet, named in its tooltip.</summary>
        private void ShowNewWords()
        {
            var words = _owner.Actions?.NewWords?.Invoke(Text) ?? [];
            _dictionary.IsEnabled = words.Count > 0;
            _dictionary.Opacity = words.Count > 0 ? 1 : 0.35;
            var tip = words.Count > 0 ? $"Add {string.Join(", ", words.Select(word => $"\u201C{word}\u201D"))} to your dictionary" : "No new words to add";
            ToolTipService.SetToolTip(_dictionary, tip);
            AutomationProperties.SetName(_dictionary, tip);
        }

        public void MarkSaved()
        {
            _original = _answer.Text;
            _meta.Text = "Saved to your vault";
            _meta.Foreground = Brush("TextTertiaryBrush");
        }

        private void SetText(string text)
        {
            if (_answer.Text == text)
            {
                return;
            }

            _setting = true;
            _answer.Text = text;
            _setting = false;
        }

        /// <summary>Other answers to pick: one tap puts it in the card (and the page, while filling) and teaches Jarvis.</summary>
        private void ShowChoices(IReadOnlyList<string> choices)
        {
            _shownChoices = choices;
            var others = choices.Where(choice => !string.Equals(choice, Text, StringComparison.OrdinalIgnoreCase)).ToList();
            _choiceRow.Visibility = others.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_choices.Children.Count == others.Count
                && _choices.Children.OfType<Button>().Select(chip => (string)chip.Tag).SequenceEqual(others))
            {
                return;
            }

            _choices.Children.Clear();
            foreach (var choice in others)
            {
                var chip = ChipButton(choice.Length > 40 ? choice[..39] + "…" : choice, () => Pick(choice));
                chip.Tag = choice;
                ToolTipService.SetToolTip(chip, choice);
                _choices.Children.Add(chip);
            }
        }

        private void Pick(string choice)
        {
            SetText(choice);
            _original = choice; // a remembered answer, not a new one to save
            _given = choice;
            _owner.Actions?.Used?.Invoke(_id, choice);
            _owner.ShowBar();
            _owner.ShowPrimary();
        }

        private async Task HearAsync()
        {
            if (_listening is { } listening)
            {
                listening.TrySetResult(); // second press: stop
                return;
            }

            if (_owner.Actions?.Hear is not { } hear)
            {
                return;
            }

            _listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((FontIcon)_mic.Content).Glyph = StopGlyph;
            ((FontIcon)_mic.Content).Foreground = Warning;
            _meta.Text = "Listening… press stop when you're done";
            _meta.Foreground = Brush("TextSecondaryBrush");
            var stop = _listening.Task;
            var heard = await Task.Run(() => hear(_id, stop)).ConfigureAwait(true);
            _listening = null;
            ((FontIcon)_mic.Content).Glyph = MicGlyph;
            ((FontIcon)_mic.Content).ClearValue(IconElement.ForegroundProperty);
            if (heard.Length > 0)
            {
                _answer.Text = heard; // counts as typed: it goes in with the top button, and can be saved
            }

            _meta.Text = heard.Length > 0 ? "Got a name wrong? Fix it, then add it to your dictionary." : "Didn't catch that. Try again, or type it.";
            _meta.Foreground = Brush("TextTertiaryBrush");
        }

        private void AddToDictionary()
        {
            if (_owner.Actions?.Remember is { } remember)
            {
                _meta.Text = remember(Text);
                _meta.Foreground = Brush("TextTertiaryBrush");
                ShowNewWords();
            }
        }

        private void Copy()
        {
            if (Text.Length == 0)
            {
                return;
            }

            var package = new DataPackage();
            package.SetText(_answer.Text);
            Clipboard.SetContent(package);
            if (!Edited)
            {
                _owner.Actions?.Used?.Invoke(_id, Text); // copying a given answer means it was right
            }

            var icon = (FontIcon)_copy.Content;
            icon.Glyph = FilledGlyph;
            var timer = _owner._dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.5);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => icon.Glyph = CopyGlyph;
            timer.Start();
        }
    }
}
