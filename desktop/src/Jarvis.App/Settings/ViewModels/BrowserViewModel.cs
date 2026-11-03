using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Core.Companion;
using Jarvis.Platform.Browser;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class BrowserViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly BrowserLinkServer _link;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _status;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    public BrowserViewModel(SettingsStore store, BrowserLinkServer link, IOptionsMonitor<BrowserOptions> options, DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _store = store;
        _link = link;
        _dispatcher = dispatcher;
        ReadPage = options.CurrentValue.ReadPage;
        Allowed = new ObservableCollection<string>(options.CurrentValue.AllowedExtensions);
        _link.ApprovalWanted += OnApprovalWanted;

        // The connection comes and goes with the browser; keep the status line honest.
        _status = dispatcher.CreateTimer();
        _status.Interval = TimeSpan.FromSeconds(1);
        _status.Tick += (_, _) => Refresh();
        _status.Start();
        Refresh();
        _ready = true;
    }

    public ObservableCollection<string> Allowed { get; }

    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "browser-extension");

    [ObservableProperty]
    public partial bool ReadPage { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Waiting { get; set; }

    [ObservableProperty]
    public partial bool HasWaiting { get; set; }

    public void Dispose()
    {
        _status.Stop();
        _link.ApprovalWanted -= OnApprovalWanted;
    }

    partial void OnReadPageChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(BrowserOptions.SectionName, nameof(BrowserOptions.ReadPage), value);
        }
    }

    [RelayCommand]
    private void Approve()
    {
        if (Waiting is { } id && !Allowed.Contains(id))
        {
            Allowed.Add(id);
            SaveAllowed();
        }

        Waiting = null;
        HasWaiting = false;
    }

    [RelayCommand]
    private void Forget(string id)
    {
        Allowed.Remove(id);
        SaveAllowed();
    }

    [RelayCommand]
    private static void OpenFolder()
        => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExtensionFolder}\"") { UseShellExecute = true });

    [RelayCommand]
    private static void CopyFolder()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(ExtensionFolder);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void SaveAllowed() => _store.Set(BrowserOptions.SectionName, nameof(BrowserOptions.AllowedExtensions), Allowed);

    private void OnApprovalWanted(string id) => _dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        Status = _link.IsConnected
            ? "Connected: your browser is linked to Jarvis."
            : Allowed.Count > 0
                ? "Not connected right now. It connects by itself while the browser is open."
                : "Not set up yet. Follow the steps below.";
        var waiting = _link.WaitingForApproval;
        if (waiting is not null && !Allowed.Contains(waiting))
        {
            Waiting = waiting;
            HasWaiting = true;
        }
    }
}
