using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Contracts.Brain;
using Jarvis.Core.Companion;
using Jarvis.Core.Providers;
using Jarvis.Providers.ClaudeCli;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class PrivacyViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ScreenPrivacy _privacy;
    private readonly IAssistantBrain _brain;
    private readonly ConversationLog _history;
    private readonly TimeProvider _time;
    private readonly TimeSpan _runningConversation;
    private readonly bool _runningShowInRecordings;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    /// <param name="brain">How long the running brain remembers (read once at start).</param>
    /// <param name="savedBrain">What settings.json holds now.</param>
    public PrivacyViewModel(
        SettingsStore store,
        ScreenPrivacy privacy,
        IOptionsMonitor<PrivacyOptions> privacyOptions,
        IOptionsMonitor<MemoryOptions> memory,
        IOptions<ClaudeCliOptions> brain,
        IOptionsMonitor<ClaudeCliOptions> savedBrain,
        IAssistantBrain assistant,
        ConversationLog history,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(privacyOptions);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(savedBrain);
        _store = store;
        _privacy = privacy;
        _brain = assistant;
        _history = history;
        _time = time;
        _runningConversation = brain.Value.FreshConversationAfter;

        PrivateMode = privacyOptions.CurrentValue.PrivateMode;
        ScreenAccess = privacyOptions.CurrentValue.ScreenAccess;
        ShowWhenLooking = privacyOptions.CurrentValue.ShowWhenLooking;
        ShowInRecordings = privacyOptions.CurrentValue.ShowInRecordings;
        LetJarvisDrive = privacyOptions.CurrentValue.LetJarvisDrive;
        DriveMode = privacyOptions.CurrentValue.AskEveryStep ? DriveModes[0] : DriveModes[1];
        _runningShowInRecordings = Jarvis.Platform.Shell.WindowChrome.ShowInRecordings;
        var saved = savedBrain.CurrentValue.FreshConversationAfter;
        Conversation = ConversationChoices.MinBy(choice => Math.Abs((choice.Length - saved).Ticks)) ?? ConversationChoices[2];
        RestartNeeded = saved != _runningConversation || ShowInRecordings != _runningShowInRecordings;
        Unload = UnloadChoices.FirstOrDefault(choice => choice.Minutes == memory.CurrentValue.UnloadVoiceAfterMinutes) ?? UnloadChoices[0];
        RefreshLooks();
        _ready = true;
    }

    public IReadOnlyList<ConversationChoice> ConversationChoices { get; } =
    [
        new("Nothing: each question stands alone", TimeSpan.Zero),
        new("5 minutes after the last question", TimeSpan.FromMinutes(5)),
        new("10 minutes after the last question", TimeSpan.FromMinutes(10)),
        new("30 minutes after the last question", TimeSpan.FromMinutes(30)),
        new("Until Jarvis restarts", TimeSpan.FromDays(365)),
    ];

    public IReadOnlyList<UnloadChoice> UnloadChoices { get; } =
    [
        new("Keep loaded (fastest replies, about 1 GB)", 0),
        new("Free after 5 idle minutes", 5),
        new("Free after 15 idle minutes", 15),
        new("Free after 1 idle hour", 60),
    ];

    public ObservableCollection<string> Looks { get; } = [];

    [ObservableProperty]
    public partial string LastLook { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool PrivateMode { get; set; }

    partial void OnPrivateModeChanged(bool value) => Save(nameof(PrivacyOptions.PrivateMode), value);

    [ObservableProperty]
    public partial bool ScreenAccess { get; set; }

    [ObservableProperty]
    public partial bool ShowWhenLooking { get; set; }

    [ObservableProperty]
    public partial ConversationChoice Conversation { get; set; }

    [ObservableProperty]
    public partial UnloadChoice Unload { get; set; }

    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    [ObservableProperty]
    public partial string ForgetMessage { get; set; } = string.Empty;

    partial void OnScreenAccessChanged(bool value) => Save(nameof(PrivacyOptions.ScreenAccess), value);

    partial void OnShowWhenLookingChanged(bool value) => Save(nameof(PrivacyOptions.ShowWhenLooking), value);

    [ObservableProperty]
    public partial bool LetJarvisDrive { get; set; }

    partial void OnLetJarvisDriveChanged(bool value) => Save(nameof(PrivacyOptions.LetJarvisDrive), value);

    public IReadOnlyList<string> DriveModes { get; } =
    [
        "Before every step",
        "Once per task, then take control (take back anytime)",
    ];

    [ObservableProperty]
    public partial string DriveMode { get; set; }

    partial void OnDriveModeChanged(string value) => Save(nameof(PrivacyOptions.AskEveryStep), value == DriveModes[0]);

    [ObservableProperty]
    public partial bool ShowInRecordings { get; set; }

    partial void OnShowInRecordingsChanged(bool value)
    {
        Save(nameof(PrivacyOptions.ShowInRecordings), value);
        if (_ready)
        {
            RestartNeeded = value != _runningShowInRecordings || Conversation.Length != _runningConversation;
        }
    }

    partial void OnConversationChanged(ConversationChoice value)
    {
        if (_ready && value is not null)
        {
            _store.Set(ClaudeCliOptions.SectionName, nameof(ClaudeCliOptions.FreshConversationAfter), value.Length.ToString("c", CultureInfo.InvariantCulture));
            RestartNeeded = value.Length != _runningConversation || ShowInRecordings != _runningShowInRecordings;
        }
    }

    partial void OnUnloadChanged(UnloadChoice value)
    {
        if (_ready && value is not null)
        {
            _store.Set(MemoryOptions.SectionName, nameof(MemoryOptions.UnloadVoiceAfterMinutes), value.Minutes);
        }
    }

    [RelayCommand]
    private void Forget()
    {
        _brain.ForgetConversation();
        _history.Clear();
        ForgetMessage = "Done. Your next question starts a fresh conversation.";
    }

    [RelayCommand]
    private void ClearLooks()
    {
        _privacy.Clear();
        RefreshLooks();
    }

    [RelayCommand]
    private static void Restart() => SettingsStore.RestartApp();

    private void Save(string key, bool value)
    {
        if (_ready)
        {
            _store.Set(PrivacyOptions.SectionName, key, value);
        }
    }

    private void RefreshLooks()
    {
        Looks.Clear();
        var looks = _privacy.Recent();
        foreach (var look in looks)
        {
            var displays = look.Displays switch { 0 => "no screenshot", 1 => "1 screen", var n => $"{n} screens" };
            Looks.Add($"{When(look.At)} · {displays} · {look.Reason}");
        }

        LastLook = looks.Count == 0
            ? "Jarvis hasn't looked at your screen yet."
            : $"Last looked at your screen {When(looks[0].At)}, when {char.ToLowerInvariant(looks[0].Reason[0])}{looks[0].Reason[1..]}.";
    }

    private string When(DateTimeOffset at)
        => TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("ddd d MMM, HH:mm:ss", CultureInfo.CurrentCulture);
}

public sealed record ConversationChoice(string Label, TimeSpan Length)
{
    public override string ToString() => Label;
}

public sealed record UnloadChoice(string Label, int Minutes)
{
    public override string ToString() => Label;
}
