using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Core.Providers;
using Jarvis.Providers.ClaudeCli;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class GeneralViewModel : ObservableObject
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Jarvis";

    /// <summary>Launched at sign-in: start quietly in the tray, without opening Settings.</summary>
    public const string BackgroundArgument = "--background";

    private static string LoginCommand => $"\"{Environment.ProcessPath}\" {BackgroundArgument}";

    /// <summary>Brings an existing start-with-Windows entry up to date (older ones opened Settings at sign-in).</summary>
    public static void RefreshLoginCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (run?.GetValue(RunValue) is string current && current != LoginCommand)
        {
            run.SetValue(RunValue, LoginCommand);
        }
    }

    private readonly SettingsStore _store;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    /// <param name="brain">The model this run of Jarvis started with (read once).</param>
    /// <param name="savedBrain">What settings.json holds now.</param>
    public GeneralViewModel(SettingsStore store, IOptions<ClaudeCliOptions> brain, IOptionsMonitor<ClaudeCliOptions> savedBrain, IOptions<ProviderOptions> providers, IOptionsMonitor<Jarvis.Core.Sound.SoundOptions> sounds)
    {
        ArgumentNullException.ThrowIfNull(sounds);
        Sounds = sounds.CurrentValue.Enabled;
        DrawingSounds = sounds.CurrentValue.DrawingSounds;
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(savedBrain);
        ArgumentNullException.ThrowIfNull(providers);
        _store = store;
        BrainModel = savedBrain.CurrentValue.Model;
        RestartNeeded = BrainModel != brain.Value.Model;
        Engines = $"Brain: Claude Code ({providers.Value.Brain}) · Speech-to-text: {providers.Value.SpeechToText} · Voice: {providers.Value.TextToSpeech}";
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        LaunchAtLogin = run?.GetValue(RunValue) is not null;
        _ready = true;
    }

    public IReadOnlyList<string> Models { get; } = ["sonnet", "opus", "haiku"];

    public string Engines { get; }

    [ObservableProperty]
    public partial bool LaunchAtLogin { get; set; }

    [ObservableProperty]
    public partial bool Sounds { get; set; }

    [ObservableProperty]
    public partial bool DrawingSounds { get; set; }

    partial void OnDrawingSoundsChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(Jarvis.Core.Sound.SoundOptions.SectionName, nameof(Jarvis.Core.Sound.SoundOptions.DrawingSounds), value);
        }
    }

    partial void OnSoundsChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(Jarvis.Core.Sound.SoundOptions.SectionName, nameof(Jarvis.Core.Sound.SoundOptions.Enabled), value);
        }
    }

    [ObservableProperty]
    public partial string BrainModel { get; set; }

    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    [RelayCommand]
    private static void Restart() => SettingsStore.RestartApp();

    /// <summary>Closing this window only hides Settings; Jarvis keeps running in the tray until it quits.</summary>
    [RelayCommand]
    private static void Quit() => ((App)Microsoft.UI.Xaml.Application.Current).Shutdown();

    partial void OnLaunchAtLoginChanged(bool value)
    {
        if (!_ready)
        {
            return;
        }

        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (value)
        {
            run.SetValue(RunValue, LoginCommand);
        }
        else
        {
            run.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    partial void OnBrainModelChanged(string value)
    {
        if (!_ready)
        {
            return;
        }

        _store.Set(ClaudeCliOptions.SectionName, nameof(ClaudeCliOptions.Model), value);
        RestartNeeded = true;
    }
}
