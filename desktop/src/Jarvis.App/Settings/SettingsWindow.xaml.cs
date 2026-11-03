using System.ComponentModel;
using Jarvis.App.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Jarvis.App.Settings;

public sealed partial class SettingsWindow : Window
{
    private readonly IServiceProvider _services;

    public SettingsWindow(SettingsViewModel viewModel, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        _services = services;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        this.ResizeDips(960, 640);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "jarvis.ico"));

        ViewModel.PropertyChanged += OnViewModelChanged;
        ShowPage();
    }

    public SettingsViewModel ViewModel { get; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSection))
        {
            ShowPage();
        }
    }

    /// <summary>A fresh page (and view model) each time, so it always shows the current settings.</summary>
    private void ShowPage()
        => PageHost.Content = ActivatorUtilities.CreateInstance(_services, ViewModel.SelectedSection.Page);
}
