using System.ComponentModel;
using Jarvis.App.Settings.ViewModels;
using Jarvis.Core.Vault;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Jarvis.App.Settings.Pages;

/// <summary>
/// "About me": the knowledge graph or the list, one search for both, a box that files anything you
/// tell it in the right place, and new topics or folders. Clicking a dot opens it on the right: a fact
/// to edit or delete; a topic to rename, edit line by line, add to or delete; a folder to delete.
/// </summary>
public sealed partial class AboutMePage : UserControl
{
    private const string Icons = "Segoe Fluent Icons,Segoe MDL2 Assets";
    private readonly KnowledgeGraphView _graph = new();

    public AboutMePage(AboutMeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        GraphHost.Child = _graph;
        _graph.Picked += node =>
        {
            ViewModel.Selected = node;
            ShowDetail(node);
        };
        ViewModel.PropertyChanged += OnViewModelChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
        _graph.Show(ViewModel.Map);
    }

    public AboutMeViewModel ViewModel { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AboutMeViewModel.Map):
                _graph.Show(ViewModel.Map);
                _graph.Search(ViewModel.Query);
                ShowDetail(ViewModel.Selected is { } picked ? ViewModel.Map.Nodes.FirstOrDefault(node => node.Id == picked.Id) : null);
                break;
            case nameof(AboutMeViewModel.Query):
                _graph.Search(ViewModel.Query);
                break;
        }
    }

    /// <summary>The side panel for what was picked.</summary>
    private void ShowDetail(GraphNode? node)
    {
        Detail.Children.Clear();
        DetailColumn.Width = node is null ? new GridLength(0) : new GridLength(260);
        if (node is null)
        {
            _graph.ClearSelection();
            return;
        }

        var close = IconButton("", "Close", () =>
        {
            ViewModel.Selected = null;
            ShowDetail(null);
        });
        var header = new Grid { ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Heading(node.Kind switch { GraphNodeKind.You => "You", GraphNodeKind.Fact => ProfileNotes.Title(node.Path), _ => node.Label }));
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        Detail.Children.Add(header);

        switch (node.Kind)
        {
            case GraphNodeKind.Fact:
                var text = new TextBox { Text = node.Text, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
                Detail.Children.Add(text);
                Detail.Children.Add(Row(
                    Accent("Save", async () => await ViewModel.SaveSelectedAsync(text.Text).ConfigureAwait(true)),
                    Confirming("Delete", "Delete this fact?", async () => await ViewModel.SaveSelectedAsync(string.Empty).ConfigureAwait(true))));
                Detail.Children.Add(Row(Explorer(node.Path, folder: false)));
                break;

            case GraphNodeKind.Topic:
                var name = new TextBox { Text = node.Label.Split(" / ")[^1], PlaceholderText = "Name" };
                Detail.Children.Add(Row(name, Plain("Rename", async () => await ViewModel.RenameTopicAsync(node.Path, name.Text).ConfigureAwait(true))));
                foreach (var row in ViewModel.FactsOf(node.Path))
                {
                    Detail.Children.Add(FactLine(row));
                }

                var add = new TextBox { PlaceholderText = "Add to this topic…", TextWrapping = TextWrapping.Wrap };
                add.KeyDown += async (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Enter)
                    {
                        e.Handled = true;
                        await ViewModel.AddToSelectedAsync(add.Text).ConfigureAwait(true);
                    }
                };
                Detail.Children.Add(add);
                Detail.Children.Add(Row(
                    Accent("Add", async () => await ViewModel.AddToSelectedAsync(add.Text).ConfigureAwait(true)),
                    Confirming("Delete topic", $"Delete {node.Label} and its {ViewModel.FactsOf(node.Path).Count} facts?", async () => await ViewModel.DeleteAsync(node.Path, folder: false).ConfigureAwait(true))));
                Detail.Children.Add(Row(Explorer(node.Path, folder: false)));
                Detail.Children.Add(Note(node.Path));
                break;

            case GraphNodeKind.Folder:
                var topics = ViewModel.Map.Nodes.Where(other => other.Kind == GraphNodeKind.Topic && KnowledgeGraph.FolderOf(other.Path) == node.Path).ToList();
                Detail.Children.Add(Note(topics.Count == 0 ? "No topics in it yet." : string.Join(" · ", topics.Select(topic => topic.Label.Split(" / ")[^1]))));
                Detail.Children.Add(Row(
                    Confirming("Delete folder", $"Delete {node.Label} and all {topics.Count} topics in it?", async () => await ViewModel.DeleteAsync(node.Path, folder: true).ConfigureAwait(true)),
                    Explorer(node.Path, folder: true)));
                Detail.Children.Add(Note("Add a topic to it with the New topic box: " + node.Text + "/name."));
                break;

            default:
                Detail.Children.Add(Note("Everything around you is what Jarvis has learned. Tell it more in the box above, answer its hunches, or click any dot to edit it."));
                Detail.Children.Add(Row(Explorer(string.Empty, folder: true)));
                break;
        }
    }

    /// <summary>One of a topic's facts, editable where it is: Enter or leaving saves, the bin deletes.</summary>
    private Grid FactLine(FactRow row)
    {
        var line = new Grid { ColumnSpacing = 4 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBox { Text = row.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        async Task SaveAsync()
        {
            row.Text = text.Text;
            await ViewModel.SaveAsync(row).ConfigureAwait(true);
        }

        text.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await SaveAsync().ConfigureAwait(true);
            }
        };
        text.LostFocus += async (_, _) => await SaveAsync().ConfigureAwait(true);
        var delete = IconButton("", "Delete this", async () => await ViewModel.DeleteAsync(row).ConfigureAwait(true));
        Grid.SetColumn(delete, 1);
        line.Children.Add(text);
        line.Children.Add(delete);
        return line;
    }

    private Button Explorer(string path, bool folder)
        => Plain("Show in Explorer", async () => await ViewModel.ShowInExplorerAsync(path, folder).ConfigureAwait(true));

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextTertiaryBrush"], TextWrapping = TextWrapping.Wrap };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static Button Plain(string label, Func<Task> run)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) => await run().ConfigureAwait(true);
        return button;
    }

    private static Button Accent(string label, Func<Task> run)
    {
        var button = Plain(label, run);
        button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        return button;
    }

    /// <summary>A delete that asks once more in place: the first click turns it into "Really?", the second does it.</summary>
    private static Button Confirming(string label, string question, Func<Task> run)
    {
        var button = new Button { Content = label };
        var armed = false;
        button.Click += async (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                button.Content = "Yes, delete";
                ToolTipService.SetToolTip(button, question);
                button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xC4, 0x2B, 0x1C));
                return;
            }

            await run().ConfigureAwait(true);
        };
        return button;
    }

    private static Button IconButton(string glyph, string name, Func<Task> run)
    {
        var button = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Content = new FontIcon { Glyph = glyph, FontFamily = new FontFamily(Icons), FontSize = 11 },
        };
        ToolTipService.SetToolTip(button, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => await run().ConfigureAwait(true);
        return button;
    }

    private static Button IconButton(string glyph, string name, Action run) => IconButton(glyph, name, () =>
    {
        run();
        return Task.CompletedTask;
    });

    private void OnGraphMode(object sender, RoutedEventArgs e) => Mode(graph: true);

    private void OnListMode(object sender, RoutedEventArgs e) => Mode(graph: false);

    private void Mode(bool graph)
    {
        GraphMode.IsChecked = graph;
        ListMode.IsChecked = !graph;
        GraphPanel.Visibility = graph ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility = graph ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecenter(object sender, RoutedEventArgs e) => _graph.Recenter();

    private async void OnOpenFolder(object sender, RoutedEventArgs e) => await ViewModel.ShowInExplorerAsync(string.Empty, folder: true).ConfigureAwait(true);

    private void OnAnythingKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            ViewModel.AddAnywhereCommand.Execute(null);
        }
    }

    private async void OnNewTopic(object sender, RoutedEventArgs e) => await MakeTopicAsync().ConfigureAwait(true);

    private async void OnNewTopicKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await MakeTopicAsync().ConfigureAwait(true);
        }
    }

    private async Task MakeTopicAsync()
    {
        await ViewModel.AddTopicAsync(NewTopic.Text).ConfigureAwait(true);
        NewTopic.Text = string.Empty;
    }

    // A fact in the list saves when the user presses Enter or leaves the line.
    private void OnFactKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && sender is FrameworkElement { DataContext: FactRow row })
        {
            e.Handled = true;
            _ = ViewModel.SaveAsync(row);
        }
    }

    private void OnFactLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FactRow row })
        {
            _ = ViewModel.SaveAsync(row);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FactRow row })
        {
            _ = ViewModel.DeleteAsync(row);
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProfileGroup group })
        {
            _ = ViewModel.AddAsync(group);
        }
    }

    private void OnNewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && sender is FrameworkElement { DataContext: ProfileGroup group })
        {
            e.Handled = true;
            _ = ViewModel.AddAsync(group);
        }
    }
}
