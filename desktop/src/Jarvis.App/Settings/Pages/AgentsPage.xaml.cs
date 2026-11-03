using Jarvis.App.Settings.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jarvis.App.Settings.Pages;

public sealed partial class AgentsPage : UserControl
{
    public AgentsPage(AgentsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public AgentsViewModel ViewModel { get; }
}
