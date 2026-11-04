using Jarvis.App.Hosting;
using Jarvis.Core.Companion;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jarvis.App.Notch;

/// <summary>
/// The notch's chat in its own window (its pop-out): the same exchanges in the same bubbles, kept
/// in step with the conversation, and a box to ask from. Answers still arrive in the notch as they
/// stream, and land here when they're done.
/// </summary>
internal sealed class ChatWindow : Window
{
    private readonly ConversationLog _history;
    private readonly Action<string> _ask;
    private readonly StackPanel _messages = new() { Spacing = 8 };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _box;

    public ChatWindow(ConversationLog history, Action<string> ask)
    {
        _history = history;
        _ask = ask;
        Title = "Jarvis chat";
        SystemBackdrop = new MicaBackdrop();
        this.ResizeDips(560, 680);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "jarvis.ico"));

        var font = (FontFamily)Application.Current.Resources["FontText"];
        _scroll = new ScrollViewer { Content = _messages, Padding = new Thickness(18, 16, 18, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _box = new TextBox { PlaceholderText = "Ask Jarvis…", FontFamily = font, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 8, 14, 8) };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Send();
            }
        };
        var send = new Button
        {
            Width = 38,
            Height = 38,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(19),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            Content = new FontIcon { Glyph = "", FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"), FontSize = 14 },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(send, "Send");
        send.Click += (_, _) => Send();

        var bar = new Grid { ColumnSpacing = 8, Margin = new Thickness(16, 8, 16, 16) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(_box);
        Grid.SetColumn(send, 1);
        bar.Children.Add(send);

        // A dark title area, as on the Settings window, instead of the system's light bar.
        var titleBar = new Grid { Padding = new Thickness(16, 0, 0, 0) };
        titleBar.Children.Add(new TextBlock { Text = "Jarvis", FontFamily = font, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(titleBar);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);
        Content = root;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        void Refresh() => dispatcher.TryEnqueue(Show);
        _history.Changed += Refresh;
        Closed += (_, _) => _history.Changed -= Refresh;
        Show();
    }

    private void Show()
    {
        var font = (FontFamily)Application.Current.Resources["FontText"];
        _messages.Children.Clear();
        var turns = _history.Recent;
        if (turns.Count == 0)
        {
            _messages.Children.Add(new TextBlock { Text = "Nothing asked yet. Ask anything below, or hold your talk keys.", FontFamily = font, Foreground = (Brush)Application.Current.Resources["TextSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) });
        }

        foreach (var turn in turns)
        {
            _messages.Children.Add(NotchPresenter.Bubble(turn.Question, mine: true, font));
            _messages.Children.Add(NotchPresenter.Bubble(turn.Answer, mine: false, font));
        }

        _scroll.UpdateLayout();
        _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true);
    }

    private void Send()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _box.Text = string.Empty;
        _ = Task.Run(() => _ask(text)); // a turn starts with a screen capture: off the UI thread
    }
}
