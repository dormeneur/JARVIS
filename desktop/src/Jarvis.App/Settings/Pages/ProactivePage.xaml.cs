using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class ProactivePage : UserControl
{
    public ProactivePage(ProactiveViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ProactiveViewModel ViewModel { get; }
}
