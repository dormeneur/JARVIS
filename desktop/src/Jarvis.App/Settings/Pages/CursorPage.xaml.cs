using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class CursorPage : UserControl
{
    public CursorPage(CursorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public CursorViewModel ViewModel { get; }
}
