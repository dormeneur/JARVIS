using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class PrivacyPage : UserControl
{
    public PrivacyPage(PrivacyViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public PrivacyViewModel ViewModel { get; }
}
