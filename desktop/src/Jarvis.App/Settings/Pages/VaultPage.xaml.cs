using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class VaultPage : UserControl
{
    public VaultPage(VaultViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public VaultViewModel ViewModel { get; }
}
