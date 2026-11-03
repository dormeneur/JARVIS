using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class RoutinesPage : UserControl
{
    public RoutinesPage(RoutinesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public RoutinesViewModel ViewModel { get; }

    private void OnRunNow(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewModel.RunNowCommand.Execute(id);
        }
    }

    private void OnRemove(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewModel.RemoveCommand.Execute(id);
        }
    }
}
