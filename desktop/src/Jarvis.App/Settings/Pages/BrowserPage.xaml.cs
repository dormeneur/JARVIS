using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class BrowserPage : UserControl
{
    public BrowserPage(BrowserViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
    }

    public BrowserViewModel ViewModel { get; }

    private void OnForget(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ViewModel.ForgetCommand.Execute(id);
        }
    }
}
