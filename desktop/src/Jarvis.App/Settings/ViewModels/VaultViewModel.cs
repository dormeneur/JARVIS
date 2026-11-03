using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Contracts.Vault;
using Jarvis.Providers.Local;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class VaultViewModel : ObservableObject
{
    private readonly IVault _vault;
    private readonly string _address;

    public VaultViewModel(IVault vault, IOptionsMonitor<VaultOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _vault = vault;
        _address = options.CurrentValue.Address;
        Refresh();
    }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string InviteCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Busy { get; set; }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (InviteCode.Trim().Length == 0)
        {
            Message = "Paste an invite code first.";
            return;
        }

        Busy = true;
        Message = "Connecting…";
        try
        {
            await _vault.ConnectAsync(InviteCode, CancellationToken.None).ConfigureAwait(true);
            InviteCode = string.Empty;
            Message = "Connected. Ask Jarvis to fill a form, or about your notes.";
        }
        catch (VaultException exception)
        {
            Message = exception.Message;
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _vault.Disconnect();
        Message = "Disconnected. To remove this PC from the vault for good, revoke it on your phone (Settings → Devices).";
        Refresh();
    }

    private void Refresh()
    {
        IsConnected = _vault.IsConnected;
        Status = IsConnected ? $"Connected to your vault at {_address} as this PC." : $"Not connected. The vault server is expected at {_address}.";
    }
}
