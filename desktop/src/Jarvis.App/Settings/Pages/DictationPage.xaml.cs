using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class DictationPage : UserControl
{
    public DictationPage(DictationViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public DictationViewModel ViewModel { get; }

    private void OnRemoveWord(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string word })
        {
            ViewModel.RemoveWordCommand.Execute(word);
        }
    }
}
