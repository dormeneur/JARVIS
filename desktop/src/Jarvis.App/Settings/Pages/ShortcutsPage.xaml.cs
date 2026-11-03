using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class ShortcutsPage : UserControl
{
    public ShortcutsPage(ShortcutsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public ShortcutsViewModel ViewModel { get; }
}
