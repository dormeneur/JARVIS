using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class ConnectorsPage : UserControl
{
    public ConnectorsPage(ConnectorsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ConnectorsViewModel ViewModel { get; }

    private void OnTest(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            ViewModel.TestRowCommand.Execute(name);
        }
    }

    private void OnSignIn(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            ViewModel.SignInRowCommand.Execute(name);
        }
    }

    private void OnConnect(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: CatalogRow row })
        {
            ViewModel.ConnectCommand.Execute(row);
        }
    }

    private void OnGoogleStep(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
        {
            ViewModel.OpenGoogleStepCommand.Execute(url);
        }
    }

    private void OnRemoveGoogle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string email })
        {
            ViewModel.RemoveGoogleCommand.Execute(email);
        }
    }

    private void OnTokenPage(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CatalogRow row })
        {
            ViewModel.OpenTokenPageCommand.Execute(row);
        }
    }

    private void OnRemove(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            ViewModel.RemoveCommand.Execute(name);
        }
    }
}
