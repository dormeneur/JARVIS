using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class MicrophonePage : UserControl
{
    public MicrophonePage(MicrophoneViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MicrophoneViewModel ViewModel { get; }
}
