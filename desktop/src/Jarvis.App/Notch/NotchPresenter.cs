using System.Numerics;
using Jarvis.Contracts.Notch;
using Jarvis.Core;
using Jarvis.Core.Cursor;
using Jarvis.Core.Notch;
using Jarvis.Platform.Shell;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jarvis.App.Notch;

/// <summary>Turns <see cref="NotchState"/> changes into notch motion and content. Safe to call from any thread.</summary>
internal sealed class NotchPresenter : INotchPresenter
{
    private const double ContentTopPadding = 6;
    private const double ComposerTopPadding = 34;
    private const double ComposerBottomGap = 16;
    private const double FadeInScrollDistance = 20;
    private static readonly TimeSpan ContentTransition = TimeSpan.FromMilliseconds(340);

    /// <summary>Text must be gone before the shape shrinks under it, or it hangs over the desktop.</summary>
    private static readonly TimeSpan ContentExit = TimeSpan.FromMilliseconds(90);

    private readonly DispatcherQueue _dispatcher;
    private readonly IOptionsMonitor<CursorOptions> _cursorOptions;
    private PauseSwitch? _pause;
    private bool _hidden;
    private DispatcherQueueTimer? _fullscreenCheck; // a field: a timer only referenced locally gets collected
    private NotchWindow? _window;
    private NotchSurface? _surface;
    private TextBlock? _responseText;
    private Grid? _responseView;
    private TextBox? _composer;
    private StackPanel? _actions;
    private TextBlock? _caption;

    /// <summary>The pill's words and three dots that breathe in turn ("Listening •••").</summary>
    private StackPanel? _activity;
    private readonly List<Microsoft.UI.Xaml.Shapes.Ellipse> _dots = [];
    private DispatcherQueueTimer? _dotTimer;
    private int _dotStep;
    private StackPanel? _history;
    private ScrollViewer? _historyView;
    private IReadOnlyList<ChatTurn>? _shownHistory;
    private (double Width, double Height)? _clickable;
    private NotchState.Composing? _composing;
    private NotchFormView? _formView;
    private NotchHomeView? _homeView;
    private NotchEventView? _eventView;
    private NotchSettingsView? _settingsView;
    private NotchState _current = NotchState.Idle;
    private Button? _close;
    private DispatcherQueueTimer? _clickAway;
    private bool _wasDown;

    public NotchPresenter(DispatcherQueue dispatcher, IOptionsMonitor<CursorOptions> cursorOptions)
    {
        _dispatcher = dispatcher;
        _cursorOptions = cursorOptions;
    }

    /// <summary>What the close button does: the same as Escape (stop the turn, close the panel). Set by the app.</summary>
    public Action? Escape { get; set; }

    /// <summary>Creates and shows the notch. Call once on the UI thread after the app has launched.</summary>
    public void Initialize(PauseSwitch pause)
    {
        ArgumentNullException.ThrowIfNull(pause);
        _window = new NotchWindow();
        _surface = new NotchSurface(_window.SurfaceHost, (float)NotchWindow.EnvelopeHeight);
        BuildResponseContent(_window.ContentArea);
        BuildComposer(_window.ContentArea);
        _formView = new NotchFormView(
            _window,
            _window.ContentArea,
            () =>
            {
                if (_clickable is { } panel)
                {
                    _window.SetClickablePanel(panel.Width, panel.Height);
                }
            },
            () => ((App)Application.Current).ShowSettings("Vault"));
        _homeView = new NotchHomeView(_window.ContentArea);
        _eventView = new NotchEventView(_window, _window.ContentArea);
        _settingsView = new NotchSettingsView(_window.ContentArea, ((App)Application.Current).Services);
        BuildClose(_window.ContentArea);
        _caption = new TextBlock
        {
            FontSize = 12,
            FontFamily = (FontFamily)Application.Current.Resources["FontText"],
            Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _caption.HorizontalAlignment = HorizontalAlignment.Left;
        _caption.Visibility = Visibility.Visible; // shown and hidden with the pill now
        _activity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Children = { _caption },
        };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 3; i++)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Fill = (Brush)Application.Current.Resources["TextSecondaryBrush"], Opacity = 0.3 };
            _dots.Add(dot);
            dots.Children.Add(dot);
        }

        _activity.Children.Add(dots);
        _window.ContentArea.Children.Add(_activity);
        _dotTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _dotTimer.Interval = TimeSpan.FromMilliseconds(360);
        _dotTimer.Tick += (_, _) =>
        {
            _dotStep = (_dotStep + 1) % 3;
            for (var i = 0; i < _dots.Count; i++)
            {
                _dots[i].Opacity = i == _dotStep ? 1 : 0.3;
            }
        };
        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                CloseComposer(submit: false); // clicked away
                _formView?.Deactivated(); // back to the page: the form panel stays
            }
        };
        _window.ShowPassively();
        Apply(NotchState.Idle);
        _pause = pause;
        pause.Changed += paused => _dispatcher.TryEnqueue(() =>
        {
            if (paused)
            {
                CloseComposer(submit: false);
            }

            UpdateVisibility();
        });

        // A click anywhere else closes a message or the home (not the form panel, nor a question waiting for an answer).
        _clickAway = _dispatcher.CreateTimer();
        _clickAway.Interval = TimeSpan.FromMilliseconds(50);
        _clickAway.Tick += (_, _) => CheckClickAway();

        // The resting sliver steps aside for fullscreen games, videos and presentations.
        _fullscreenCheck = _dispatcher.CreateTimer();
        _fullscreenCheck.Interval = TimeSpan.FromSeconds(2);
        _fullscreenCheck.Tick += (_, _) => UpdateVisibility();
        _fullscreenCheck.Start();
    }

    public void Show(NotchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _dispatcher.TryEnqueue(() => Apply(state));
    }

    /// <summary>Hidden while paused, or at rest while something is fullscreen; shown otherwise.</summary>
    private void UpdateVisibility()
    {
        if (_window is null)
        {
            return;
        }

        var atRest = _current is NotchState.IdleState or NotchState.Working;
        var hide = _pause?.IsPaused == true || (atRest && _cursorOptions.CurrentValue.HideInFullscreen && Fullscreen.IsActive);
        if (hide == _hidden)
        {
            return;
        }

        _hidden = hide;
        if (hide)
        {
            _window.AppWindow.Hide();
        }
        else
        {
            _window.ShowPassively();
        }
    }

    private void Apply(NotchState state)
    {
        if (_window is null || _surface is null || _responseText is null || _responseView is null || _composer is null)
        {
            return;
        }

        if (state is not NotchState.IdleState)
        {
            _window.RaiseToTop();
        }

        var frame = NotchLayout.FrameFor(state);
        _surface.Apply(frame, NotchAccent.GlowFor(state), NotchAccent.GlowMoves(state), state is NotchState.Working, NotchAccent.GlowReach(state), NotchAccent.GlowStrength(state));

        var wasComposing = _composing is not null;
        _composing = state as NotchState.Composing;
        if (wasComposing && _composing is null)
        {
            _window.EndTyping();
        }

        var actions = (state as NotchState.Response)?.Actions ?? [];
        ShowActions(actions);
        // Every open panel takes clicks: it has a close button.
        (double, double)? clickable = state is NotchState.Response or NotchState.Form or NotchState.Home or NotchState.EventDraft or NotchState.Settings ? (frame.Width, frame.Height) : null;
        if (clickable != _clickable)
        {
            _clickable = clickable;
            _window.SetClickablePanel(clickable?.Item1, clickable?.Item2 ?? 0);
        }

        var area = _window.ContentArea;
        // The live states say what's happening in words, as a soft pill: "Listening •••", "Thinking •••".
        var caption = state switch
        {
            NotchState.Listening => "Listening",
            NotchState.Dictating => "Dictating",
            NotchState.Thinking thinking => thinking.Caption ?? "Thinking",
            _ => null,
        };
        if (state is NotchState.Form form)
        {
            _formView?.Show(form);
        }
        else
        {
            _formView?.Hide();
        }

        if (state is NotchState.Home home)
        {
            _homeView?.Show(home);
        }
        else
        {
            _homeView?.Hide();
        }

        if (state is NotchState.EventDraft draft)
        {
            _eventView?.Show(draft);
        }
        else
        {
            _eventView?.Hide();
        }

        // Settings take the keyboard while open (pages have text boxes and lists); closing hands it back.
        if (state is NotchState.Settings settings)
        {
            if (_current is not NotchState.Settings)
            {
                _window.BeginTyping();
            }

            _settingsView?.Show(settings);
        }
        else if (_current is NotchState.Settings)
        {
            _settingsView?.Hide();
            _window.EndTyping();
        }

        if (state is NotchState.Response or NotchState.Composing or NotchState.Form or NotchState.Home or NotchState.EventDraft or NotchState.Settings || caption is not null)
        {
            _responseView.Visibility = state is NotchState.Response ? Visibility.Visible : Visibility.Collapsed;
            _composer.Visibility = state is NotchState.Composing ? Visibility.Visible : Visibility.Collapsed;
            ShowHistory((state as NotchState.Composing)?.History ?? []);
            _activity!.Visibility = caption is null ? Visibility.Collapsed : Visibility.Visible;
            _caption!.Text = caption ?? string.Empty;
            if (caption is null)
            {
                _dotTimer?.Stop();
            }
            else if (_dotTimer is { IsRunning: false })
            {
                _dotTimer.Start();
            }
            if (state is NotchState.Response response)
            {
                _responseText.Text = response.Text;
            }

            area.Width = frame.Width - (2 * NotchLayout.ShoulderRadius);
            area.Height = frame.Height;
            if (_composing is not null)
            {
                if (!wasComposing)
                {
                    _composer.Text = string.Empty;
                }

                _window.BeginTyping();
                _composer.Focus(FocusState.Programmatic);
            }

            if (_current.GetType() != state.GetType())
            {
                area.OpacityTransition.Duration = ContentTransition;
                area.Scale = new Vector3(0.92f, 0.92f, 1);
                area.Opacity = 1;
                area.Scale = Vector3.One;
            }
        }
        else
        {
            area.OpacityTransition.Duration = ContentExit;
            area.Opacity = 0;
        }

        _current = state;
        UpdateVisibility();
        if (_close is not null)
        {
            // The form panel has its own, in its header; the composer and every other open panel get this one.
            _close.Visibility = state is NotchState.Response or NotchState.Home or NotchState.Composing or NotchState.EventDraft or NotchState.Settings ? Visibility.Visible : Visibility.Collapsed;
        }

        if (ClosesOnClickAway(state))
        {
            _wasDown = Jarvis.Platform.Shell.Mouse.LeftDown;
            _clickAway?.Start();
        }
        else
        {
            _clickAway?.Stop();
        }
    }

    private static bool ClosesOnClickAway(NotchState state) => state is NotchState.Home or NotchState.Response { KeepOnClickAway: false };

    /// <summary>A fresh press outside the panel closes it (the answer may still be speaking; only Escape stops that).</summary>
    private void CheckClickAway()
    {
        var down = Jarvis.Platform.Shell.Mouse.LeftDown;
        var pressed = down && !_wasDown;
        _wasDown = down;
        if (!pressed || _window is null || !ClosesOnClickAway(_current))
        {
            return;
        }

        var frame = NotchLayout.FrameFor(_current);
        var (x, y) = Jarvis.Platform.Shell.Mouse.Position;
        if (!_window.IsOnPanel(x, y, frame.Width, frame.Height))
        {
            Apply(NotchState.Idle);
        }
    }

    /// <summary>The × in the top-right corner of an open panel: Escape, by mouse.</summary>
    private void BuildClose(Grid area)
    {
        _close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), FontSize = 10 },
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_close, "Close (Esc)");
        ToolTipService.SetToolTip(_close, "Close (Esc)");
        _close.Click += (_, _) => CloseNow();
        area.Children.Add(_close);
    }

    private void CloseNow()
    {
        if (_composing is not null)
        {
            CloseComposer(submit: false);
            return;
        }

        if (Escape is { } escape)
        {
            _ = Task.Run(escape);
        }
        else
        {
            Apply(NotchState.Idle);
        }
    }

    private void BuildComposer(Grid area)
    {
        _composer = new TextBox
        {
            PlaceholderText = "Ask about anything on your screen…",
            FontSize = 13,
            FontFamily = (FontFamily)Application.Current.Resources["FontText"],
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 8, 0, 8),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, ComposerTopPadding - 8, NotchLayout.ContentHorizontalPadding, ComposerBottomGap),
            Visibility = Visibility.Collapsed,
        };

        // The notch is the box: no field background or focus underline of its own.
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused" })
        {
            _composer.Resources[key] = clear;
        }

        _composer.Resources["TextControlForegroundFocused"] = _composer.Foreground;
        _composer.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && _composer.Text.Trim().Length > 0)
            {
                e.Handled = true;
                CloseComposer(submit: true);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                CloseComposer(submit: false);
            }
        };
        area.Children.Add(_composer);

        _history = new StackPanel { Spacing = 10 };
        _historyView = new ScrollViewer
        {
            Content = _history,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 14, NotchLayout.ContentHorizontalPadding, 64),
            Visibility = Visibility.Collapsed,
        };
        area.Children.Add(_historyView);
    }

    /// <summary>Rebuilds the button row: 24 DIP pills, the first one filled. A click runs it and closes the panel.</summary>
    private void ShowActions(IReadOnlyList<NotchAction> actions)
    {
        if (_actions is null)
        {
            return;
        }

        _actions.Children.Clear();
        _actions.Visibility = actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            var button = new Button
            {
                Content = action.Label,
                Height = 24,
                MinWidth = 64,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(12),
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["FontText"],
            };
            if (i == 0)
            {
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }

            button.Click += (_, _) =>
            {
                Apply(NotchState.Idle);
                _ = Task.Run(action.Invoke);
            };
            _actions.Children.Add(button);
        }
    }

    /// <summary>
    /// The chat above the text box: each question (muted, right) and answer (left), newest at the
    /// bottom and scrolled into view. With history the box sits at the bottom of a full-height panel.
    /// </summary>
    private void ShowHistory(IReadOnlyList<ChatTurn> turns)
    {
        if (_history is null || _historyView is null || _composer is null || ReferenceEquals(turns, _shownHistory))
        {
            return;
        }

        _shownHistory = turns;
        _history.Children.Clear();
        _historyView.Visibility = turns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _composer.VerticalAlignment = turns.Count > 0 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        _composer.Margin = turns.Count > 0
            ? new Thickness(NotchLayout.ContentHorizontalPadding, 0, NotchLayout.ContentHorizontalPadding, ComposerBottomGap - 8)
            : new Thickness(NotchLayout.ContentHorizontalPadding, ComposerTopPadding - 8, NotchLayout.ContentHorizontalPadding, ComposerBottomGap);

        var font = (FontFamily)Application.Current.Resources["FontText"];
        foreach (var turn in turns)
        {
            _history.Children.Add(new TextBlock
            {
                Text = turn.Question,
                FontSize = 12,
                FontFamily = font,
                Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            });
            _history.Children.Add(new TextBlock
            {
                Text = turn.Answer,
                FontSize = 13,
                LineHeight = 18,
                FontFamily = font,
                Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
        }

        _historyView.UpdateLayout();
        _historyView.ChangeView(null, _historyView.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>Hands focus back first, so the question is about the app the user was in.</summary>
    private void CloseComposer(bool submit)
    {
        if (_composing is not { } composing || _window is null || _composer is null)
        {
            return;
        }

        _composing = null;
        _window.EndTyping();
        var text = _composer.Text.Trim();

        // Off the UI thread: a turn starts with a screen capture.
        _ = Task.Run(submit ? () => composing.Submit(text) : composing.Dismiss);
    }

    private void BuildResponseContent(Grid area)
    {
        _responseText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 18,
            FontFamily = (FontFamily)Application.Current.Resources["FontText"],
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, ContentTopPadding, NotchLayout.ContentHorizontalPadding + 22, 15),
        };

        area.Opacity = 0;
        area.OpacityTransition = new ScalarTransition { Duration = ContentTransition };
        area.ScaleTransition = new Vector3Transition { Duration = ContentTransition };
        var scroller = new ScrollViewer { Content = _responseText, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        var fade = NotchTopFade.Create();
        fade.Opacity = 0;

        // Only fade text that has actually scrolled under the top edge; unscrolled text stays crisp.
        scroller.ViewChanged += (_, _) => fade.Opacity = Math.Min(1, scroller.VerticalOffset / FadeInScrollDistance);
        _responseView = new Grid();
        _responseView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _responseView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 0, NotchLayout.ContentHorizontalPadding, 14),
        };
        Grid.SetRow(_actions, 1);
        _responseView.Children.Add(scroller);
        _responseView.Children.Add(fade);
        _responseView.Children.Add(_actions);
        area.Children.Add(_responseView);
        area.SizeChanged += (_, _) => area.CenterPoint = new Vector3((float)(area.ActualWidth / 2), 0, 0);
    }
}
