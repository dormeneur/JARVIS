using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Core.Dictation;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class DictationViewModel : ObservableObject
{
    private readonly SettingsStore _store;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    public DictationViewModel(SettingsStore store, IOptionsMonitor<DictationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        var current = options.CurrentValue;
        Cleanup = current.Cleanup;
        SelectedLanguage = Languages.FirstOrDefault(language => language.Code == current.Language) ?? Languages[0];
        Words = new ObservableCollection<string>(current.Words);
        _ready = true;
    }

    /// <summary>Languages Whisper handles well; "Auto-detect" follows you across languages.</summary>
    public IReadOnlyList<LanguageChoice> Languages { get; } =
    [
        new("Auto-detect", string.Empty),
        new("English", "en"),
        new("Hindi", "hi"),
        new("Spanish", "es"),
        new("French", "fr"),
        new("German", "de"),
        new("Portuguese", "pt"),
        new("Italian", "it"),
        new("Japanese", "ja"),
        new("Chinese", "zh"),
    ];

    public ObservableCollection<string> Words { get; }

    public string WordCount => $"{Words.Count} of {DictationOptions.MaximumWords} words";

    [ObservableProperty]
    public partial bool Cleanup { get; set; }

    [ObservableProperty]
    public partial LanguageChoice SelectedLanguage { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddWordCommand))]
    public partial string NewWord { get; set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(CanAddWord))]
    private void AddWord()
    {
        Words.Add(NewWord.Trim());
        NewWord = string.Empty;
        SaveWords();
    }

    [RelayCommand]
    private void RemoveWord(string word)
    {
        Words.Remove(word);
        SaveWords();
    }

    private bool CanAddWord()
        => NewWord.Trim().Length > 0
           && Words.Count < DictationOptions.MaximumWords
           && !Words.Contains(NewWord.Trim(), StringComparer.OrdinalIgnoreCase);

    private void SaveWords()
    {
        _store.Set(DictationOptions.SectionName, nameof(DictationOptions.Words), Words);
        OnPropertyChanged(nameof(WordCount));
        AddWordCommand.NotifyCanExecuteChanged();
    }

    partial void OnCleanupChanged(bool value)
    {
        if (_ready)
        {
            _store.Set(DictationOptions.SectionName, nameof(DictationOptions.Cleanup), value);
        }
    }

    partial void OnSelectedLanguageChanged(LanguageChoice value)
    {
        if (_ready)
        {
            _store.Set(DictationOptions.SectionName, nameof(DictationOptions.Language), value.Code);
        }
    }
}

public sealed record LanguageChoice(string Label, string Code)
{
    public override string ToString() => Label;
}
