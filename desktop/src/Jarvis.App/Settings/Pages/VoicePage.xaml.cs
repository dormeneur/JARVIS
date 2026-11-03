using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class VoicePage : UserControl
{
    public VoicePage(VoiceViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public VoiceViewModel ViewModel { get; }
}
