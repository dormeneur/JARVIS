using CommunityToolkit.Mvvm.ComponentModel;
using Jarvis.App.Settings.Pages;

namespace Jarvis.App.Settings;

public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel()
    {
        SelectedSection = Sections[0];
    }

    /// <summary>Settings tabs, in sidebar order. Each opens its page.</summary>
    public IReadOnlyList<SettingsSection> Sections { get; } =
    [
        new("General", "", typeof(GeneralPage)),
        new("Privacy & memory", "", typeof(PrivacyPage)),
        new("Voice", "", typeof(VoicePage)),
        new("Microphone", "", typeof(MicrophonePage)),
        new("Dictation", "", typeof(DictationPage)),
        new("Shortcuts", "", typeof(ShortcutsPage)),
        new("Cursor", "", typeof(CursorPage)),
        new("Agents", "", typeof(AgentsPage)),
        new("Routines", "", typeof(RoutinesPage)),
        new("Proactive", "", typeof(ProactivePage)),
        new("Connectors", "", typeof(ConnectorsPage)),
        new("Browser", "", typeof(BrowserPage)),
    ];

    [ObservableProperty]
    public partial SettingsSection SelectedSection { get; set; }
}

/// <param name="Glyph">Segoe Fluent Icons code point.</param>
/// <param name="Page">The page shown for this section.</param>
public sealed record SettingsSection(string Title, string Glyph, Type Page);
