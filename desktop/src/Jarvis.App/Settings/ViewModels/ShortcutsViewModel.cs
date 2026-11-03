using CommunityToolkit.Mvvm.ComponentModel;
using Jarvis.Core.Input;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class ShortcutsViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    public ShortcutsViewModel(SettingsStore store, IOptionsMonitor<ShortcutOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        Talk = options.CurrentValue.Talk;
        Dictate = options.CurrentValue.Dictate;
        _ready = true;
    }

    public IReadOnlyList<string> Choices => ShortcutOptions.HoldChoices;

    [ObservableProperty]
    public partial string Talk { get; set; }

    [ObservableProperty]
    public partial string Dictate { get; set; }

    [ObservableProperty]
    public partial bool Clash { get; set; }

    partial void OnTalkChanged(string value) => Save();

    partial void OnDictateChanged(string value) => Save();

    /// <summary>Both chords are saved together, and only when they differ: one chord can't do two things.</summary>
    private void Save()
    {
        if (!_ready)
        {
            return;
        }

        Clash = Talk == Dictate;
        if (!Clash)
        {
            _store.Set(ShortcutOptions.SectionName, nameof(ShortcutOptions.Talk), Talk);
            _store.Set(ShortcutOptions.SectionName, nameof(ShortcutOptions.Dictate), Dictate);
        }
    }
}
