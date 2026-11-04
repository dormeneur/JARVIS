using System.Numerics;
using Jarvis.Contracts.Notch;
using Jarvis.Core;
using Jarvis.Core.Cursor;
using Jarvis.Core.Notch;
using Jarvis.Platform.Shell;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>The chat's bottom bar: the hold-to-talk pill, the text box in its own pill, and send.</summary>
    private Grid? _composerBar;
    private TextBlock? _talkLabel;

    /// <summary>Held from the talk pill: watches the mouse button, since the pill itself goes away while listening.</summary>
    private DispatcherQueueTimer? _talkWatch;
    private Action<bool>? _talking;
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

    /// <summary>Top left on the home and the chat: switch between them.</summary>
    private StackPanel? _modes;
    private Button? _homeMode;
    private Button? _chatMode;
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
        _settingsView = new NotchSettingsView(_window.ContentArea, ((App)Application.Current).Services, Collapse);
        BuildPillButton(_window.ContentArea);
        BuildClose(_window.ContentArea);
        BuildModes(_window.ContentArea);
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
                if (_composing is not null)
                {
                    Collapse(); // clicked away: the chat waits, as it is, for the next hover
                }

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
        Volatile.Write(ref _requested, state);
        _dispatcher.TryEnqueue(() => Apply(state));
    }

    /// <summary>What was last asked to show (from any thread).</summary>
    public NotchState Current => Volatile.Read(ref _requested);

    private NotchState _requested = NotchState.Idle;

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
        // A form put aside keeps working behind its pill (NotchParking has the rules).
        var (show, keep) = NotchParking.Next(state, _parked, _current);
        _parked = keep;
        if (show is null)
        {
            return;
        }

        state = show;

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
            NotchState.Parked parked => parked.Caption,
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
            _composerBar!.Visibility = state is NotchState.Composing ? Visibility.Visible : Visibility.Collapsed;
            ShowHistory((state as NotchState.Composing)?.History ?? []);
            _activity!.Visibility = caption is null ? Visibility.Collapsed : Visibility.Visible;
            _caption!.Text = caption ?? string.Empty;
            if (caption is null || state is NotchState.Parked { Busy: false })
            {
                _dotTimer?.Stop();
                foreach (var dot in _dots)
                {
                    dot.Opacity = state is NotchState.Parked ? 0 : 0.3; // a ready pill has no dots
                }
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
                if (!wasComposing && !_restoring)
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
        ShowModes(state);
        UpdateVisibility();
        if (_close is not null)
        {
            // The form panel has its own, in its header; the composer and every other open panel get this one.
            _close.Visibility = state is NotchState.Response or NotchState.Home or NotchState.Composing or NotchState.EventDraft or NotchState.Settings ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_pillButton is not null)
        {
            _pillButton.Visibility = state is NotchState.Parked ? Visibility.Visible : Visibility.Collapsed;
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

    private static bool ClosesOnClickAway(NotchState state) => state is NotchState.Home or NotchState.Settings or NotchState.Form or NotchState.Response { KeepOnClickAway: false };

    /// <summary>The panel put aside by Esc or a click outside, to bring back as it was.</summary>
    private NotchState? _parked;
    private DateTimeOffset _parkedAt;

    /// <summary>Put aside longer than this, the next hover opens the home instead.</summary>
    private static readonly TimeSpan ParkedFor = TimeSpan.FromMinutes(30);

    private bool _restoring;

    /// <summary>
    /// Esc or a click outside: Settings and the chat are put aside exactly as they are and the
    /// notch rests; a form shrinks to a pill and keeps working. Anything else just closes.
    /// </summary>
    private void Collapse()
    {
        switch (_current)
        {
            case NotchState.Settings or NotchState.Composing:
                _parked = _current;
                _parkedAt = DateTimeOffset.UtcNow;
                if (_composing is not null && _window is not null)
                {
                    _composing = null; // not the chat's Dismiss: nothing is cancelled
                    _window.EndTyping();
                }

                Apply(NotchState.Idle);
                break;
            case NotchState.Form form:
                _parked = form;
                _parkedAt = DateTimeOffset.UtcNow;
                Apply(NotchParking.PillFor(form));
                break;
            default:
                Apply(NotchState.Idle);
                break;
        }
    }

    public bool Restore()
    {
        if (_parked is not { } parked)
        {
            return false;
        }

        _parked = null;
        if (DateTimeOffset.UtcNow - _parkedAt > ParkedFor && parked is not NotchState.Form)
        {
            return false;
        }

        _restoring = true;
        try
        {
            Apply(parked);
        }
        finally
        {
            _restoring = false;
        }

        return true;
    }

    private Button? _pillButton;

    /// <summary>The whole pill is a button back to the panel it stands for.</summary>
    private void BuildPillButton(Grid area)
    {
        _pillButton = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed,
        };
        _pillButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_pillButton, "Open the form again");
        _pillButton.Click += (_, _) => Restore();
        area.Children.Add(_pillButton);
    }

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
            Collapse();
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

    /// <summary>Two round buttons in a glass pill, top left: the home, and the chat.</summary>
    private void BuildModes(Grid area)
    {
        Button Mode(string glyph, string name, Action go)
        {
            var button = new Button
            {
                Width = 30,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"] },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => go();
            return button;
        }

        _homeMode = Mode("\uE80F", "Home", () =>
        {
            if (_current is NotchState.Composing && _window is not null)
            {
                // Straight over to the home: closing the chat the usual way would end the turn and
                // could land after the home opens and hide it.
                _composing = null;
                _window.EndTyping();
                ((App)Application.Current).ShowHome();
            }
        });
        _chatMode = Mode("\uE8BD", "Chat", () =>
        {
            if (_current is NotchState.Home)
            {
                // Straight from the home's shape to the chat's: no collapse in between.
                var companion = ((App)Application.Current).Services.GetRequiredService<Jarvis.Core.Companion.CompanionSession>();
                _ = Task.Run(companion.Compose);
            }
        });
        _modes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(16),
            Background = NotchFormView.Glass,
            BorderBrush = NotchFormView.GlassEdge,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 10, 0, 0),
            Visibility = Visibility.Collapsed,
            Children = { _homeMode, _chatMode },
        };
        area.Children.Add(_modes);
    }

    /// <summary>Shows the switch on the home and the chat, with the one you're on lit.</summary>
    private void ShowModes(NotchState state)
    {
        if (_modes is null || _homeMode is null || _chatMode is null)
        {
            return;
        }

        _modes.Visibility = state is NotchState.Home or NotchState.Composing ? Visibility.Visible : Visibility.Collapsed;
        var lit = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x33, 0x80, 0xFF));
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _homeMode.Background = state is NotchState.Home ? lit : clear;
        _chatMode.Background = state is NotchState.Composing ? lit : clear;
    }

    private void CloseNow()
    {
        if (_current is NotchState.Settings)
        {
            Collapse();
            return;
        }

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
        var font = (FontFamily)Application.Current.Resources["FontText"];
        _composer = new TextBox
        {
            PlaceholderText = "Ask Jarvis…",
            FontSize = 13,
            FontFamily = font,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6),
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The pill is the box: no field background or focus underline of its own.
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
                Collapse(); // put aside with what's typed; the next hover brings it back
            }
        };

        // Hold to talk, like the keys: press, speak, let go.
        _talkLabel = new TextBlock { FontFamily = font, FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        var talk = new Button
        {
            Height = 38,
            Padding = new Thickness(14, 0, 16, 0),
            CornerRadius = new CornerRadius(19),
            Background = NotchFormView.Glass,
            BorderBrush = NotchFormView.GlassEdge,
            BorderThickness = new Thickness(1),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE720", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"] },
                    _talkLabel,
                },
            },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(talk, "Hold to talk");
        talk.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => StartTalk()), handledEventsToo: true);

        var box = new Border
        {
            Height = 38,
            Padding = new Thickness(16, 0, 6, 0),
            CornerRadius = new CornerRadius(19),
            Background = NotchFormView.Glass,
            BorderBrush = NotchFormView.GlassEdge,
            BorderThickness = new Thickness(1),
            Child = _composer,
        };
        var send = new Button
        {
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(19),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = new FontIcon { Glyph = "\uE74A", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), FontSize = 14 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(send, "Send");
        send.Click += (_, _) =>
        {
            if (_composer.Text.Trim().Length > 0)
            {
                CloseComposer(submit: true);
            }
        };

        _composerBar = new Grid
        {
            ColumnSpacing = 8,
            Height = 38,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 0, NotchLayout.ContentHorizontalPadding, 14),
            Visibility = Visibility.Collapsed,
        };
        _composerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _composerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _composerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _composerBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _composerBar.Children.Add(talk);
        Grid.SetColumn(box, 1);
        _composerBar.Children.Add(box);
        Grid.SetColumn(send, 2);
        _composerBar.Children.Add(send);

        // The chat in its own window, to keep it open beside your work.
        var popOut = new Button
        {
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(19),
            Background = NotchFormView.Glass,
            BorderBrush = NotchFormView.GlassEdge,
            BorderThickness = new Thickness(1),
            Content = new FontIcon { Glyph = "", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), FontSize = 13, Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"] },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(popOut, "Open the chat in a window");
        ToolTipService.SetToolTip(popOut, "Open the chat in a window");
        popOut.Click += (_, _) =>
        {
            CloseComposer(submit: false);
            ((App)Application.Current).ShowChatWindow();
        };
        Grid.SetColumn(popOut, 3);
        _composerBar.Children.Add(popOut);
        area.Children.Add(_composerBar);

        _talkWatch = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _talkWatch.Interval = TimeSpan.FromMilliseconds(40);
        _talkWatch.Tick += (_, _) =>
        {
            if (!Jarvis.Platform.Shell.Mouse.LeftDown)
            {
                EndTalk();
            }
        };

        _history = new StackPanel { Spacing = 8 };
        _historyView = new ScrollViewer
        {
            Content = _history,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Margin = new Thickness(NotchLayout.ContentHorizontalPadding, 46, NotchLayout.ContentHorizontalPadding, 38 + 14 + 10), // below the Home/Chat switch
            Visibility = Visibility.Collapsed,
        };
        area.Children.Add(_historyView);
    }

    /// <summary>The talk pill went down: the chat closes and the microphone opens until the button comes up.</summary>
    private void StartTalk()
    {
        if (_composing is not { Talk: { } talk } || _talking is not null || _window is null)
        {
            return;
        }

        _talking = talk;
        _composing = null;
        _window.EndTyping();
        _talkWatch?.Start();
        _ = Task.Run(() => talk(true));
    }

    private void EndTalk()
    {
        _talkWatch?.Stop();
        if (Interlocked.Exchange(ref _talking, null) is { } talk)
        {
            _ = Task.Run(() => talk(false));
        }
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
    /// The chat above the bar: each question in a bubble on the right, each answer in a glass
    /// bubble on the left, a time between exchanges more than a few minutes apart, newest at the
    /// bottom and scrolled into view.
    /// </summary>
    private void ShowHistory(IReadOnlyList<ChatTurn> turns)
    {
        if (_history is null || _historyView is null || _talkLabel is null || ReferenceEquals(turns, _shownHistory))
        {
            return;
        }

        _shownHistory = turns;
        var keys = ((App)Application.Current).Services.GetRequiredService<IOptionsMonitor<Jarvis.Core.Input.ShortcutOptions>>().CurrentValue.Talk;
        _talkLabel.Text = $"Hold {keys} to talk";
        _history.Children.Clear();
        _historyView.Visibility = turns.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var font = (FontFamily)Application.Current.Resources["FontText"];
        DateTimeOffset? previous = null;
        foreach (var turn in turns)
        {
            if (turn.At is { } at && (previous is null || at - previous > TimeSpan.FromMinutes(5)))
            {
                _history.Children.Add(new TextBlock
                {
                    Text = (at.Date == DateTimeOffset.Now.Date ? "Today " : at.ToString("ddd d MMM ", System.Globalization.CultureInfo.CurrentCulture)) + at.ToString("t", System.Globalization.CultureInfo.CurrentCulture),
                    FontSize = 11,
                    FontFamily = font,
                    Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            previous = turn.At ?? previous;
            _history.Children.Add(Bubble(turn.Question, mine: true, font));
            _history.Children.Add(Bubble(turn.Answer, mine: false, font));
        }

        _historyView.UpdateLayout();
        _historyView.ChangeView(null, _historyView.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>A chat bubble: the user's on the right in the reply blue, Jarvis's on the left in glass.</summary>
    internal static Border Bubble(string text, bool mine, FontFamily font) => new()
    {
        HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        MaxWidth = mine ? 380 : 440,
        Padding = new Thickness(12, 8, 12, 9),
        CornerRadius = mine ? new CornerRadius(14, 14, 4, 14) : new CornerRadius(14, 14, 14, 4),
        Background = mine ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x33, 0x80, 0xFF)) : NotchFormView.Glass,
        BorderBrush = NotchFormView.GlassEdge,
        BorderThickness = new Thickness(mine ? 0 : 1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 13,
            LineHeight = 18,
            FontFamily = font,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        },
    };

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
