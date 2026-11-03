using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Contracts.Audio;
using Jarvis.Core.Companion;
using Jarvis.Platform.Audio;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class MicrophoneViewModel : ObservableObject
{
    private static readonly TimeSpan TestLength = TimeSpan.FromSeconds(10);

    private readonly IMicrophone _microphone;
    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsStore _store;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    public MicrophoneViewModel(IMicrophone microphone, DispatcherQueue dispatcher, SettingsStore store, IOptionsMonitor<MicrophoneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _microphone = microphone;
        _dispatcher = dispatcher;
        _store = store;
        Devices = [new(string.Empty, "Windows default"), .. WasapiMicrophone.Devices().Select(device => new MicrophoneChoice(device.Id, device.Name))];
        SelectedDevice = Devices.FirstOrDefault(device => device.Id == options.CurrentValue.DeviceId) ?? Devices[0];
        Blocked = WasapiMicrophone.BlockedByWindows();
        _ready = true;
    }

    public IReadOnlyList<MicrophoneChoice> Devices { get; }

    /// <summary>Windows' privacy settings keep desktop apps off the microphone.</summary>
    [ObservableProperty]
    public partial bool Blocked { get; set; }

    [RelayCommand]
    private void OpenWindowsSettings()
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MicrophoneBlockedException.SettingsPage) { UseShellExecute = true });
    }

    [RelayCommand]
    private void CheckAgain() => Blocked = WasapiMicrophone.BlockedByWindows();

    [ObservableProperty]
    public partial MicrophoneChoice SelectedDevice { get; set; }

    /// <summary>0–1, roughly how loud the microphone is right now.</summary>
    [ObservableProperty]
    public partial double Level { get; set; }

    [ObservableProperty]
    public partial string Verdict { get; set; } = "Say something while the test runs. If the bar moves, your microphone works.";

    /// <summary>Listens for 10 seconds and shows the level live; running it again stops early.</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestAsync(CancellationToken cancellationToken)
    {
        var loudest = 0.0;
        IMicrophoneSession session;
        try
        {
            session = _microphone.Start(AudioFormat.SpeechMono16K);
        }
        catch (MicrophoneBlockedException)
        {
            Blocked = true;
            Verdict = "Windows is blocking desktop apps from the microphone. Use the button above to allow it.";
            return;
        }

        await using var _ = session;
        using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        window.CancelAfter(TestLength);
        try
        {
            await foreach (var chunk in session.Audio.ReadAllAsync(window.Token))
            {
                var level = AudioLevel.Of(chunk.Span);
                loudest = Math.Max(loudest, level);
                _dispatcher.TryEnqueue(() => Level = level);
            }
        }
        catch (OperationCanceledException)
        {
            // The ten seconds are up, or the user stopped the test.
        }

        Level = 0;
        Verdict = loudest > 0.15
            ? "Your microphone works."
            : "That was very quiet. Check the microphone in Windows sound settings, or speak closer to it.";
    }

    partial void OnSelectedDeviceChanged(MicrophoneChoice value)
    {
        if (_ready && value is not null)
        {
            _store.Set(MicrophoneOptions.SectionName, nameof(MicrophoneOptions.DeviceId), value.Id);
        }
    }
}

/// <param name="Id">Endpoint ID; empty for the Windows default.</param>
public sealed record MicrophoneChoice(string Id, string Name)
{
    public override string ToString() => Name;
}
