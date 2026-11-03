using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class GeneralPage : UserControl
{
    public GeneralPage(GeneralViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public GeneralViewModel ViewModel { get; }
}
