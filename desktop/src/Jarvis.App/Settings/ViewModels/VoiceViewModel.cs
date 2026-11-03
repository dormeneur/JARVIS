using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Speech;
using Jarvis.Core.Companion;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class VoiceViewModel : ObservableObject
{
    private const string PreviewLine = "Hi, I'm Jarvis. This is how I'll sound when I talk you through things.";

    private readonly SettingsStore _store;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;
    private readonly ITextToSpeech _speech;
    private readonly IAudioPlayer _player;

    public VoiceViewModel(SettingsStore store, IOptionsMonitor<CompanionOptions> options, ITextToSpeech speech, IAudioPlayer player)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _speech = speech;
        _player = player;
        var current = options.CurrentValue;
        Voices = AvailableVoices();
        SelectedVoice = Voices.FirstOrDefault(voice => voice.Id == current.Voice) ?? Voices.FirstOrDefault();
        Speed = current.Speed;
        CaptionSeconds = current.CaptionSeconds;
        _ready = true;
    }

    public IReadOnlyList<VoiceChoice> Voices { get; }

    [ObservableProperty]
    public partial VoiceChoice? SelectedVoice { get; set; }

    [ObservableProperty]
    public partial double Speed { get; set; }

    [ObservableProperty]
    public partial double CaptionSeconds { get; set; }

    [RelayCommand]
    private Task PreviewAsync(CancellationToken cancellationToken)
        => _player.PlayAsync(Speak(PreviewLine, cancellationToken), _speech.OutputFormat, cancellationToken);

    partial void OnSelectedVoiceChanged(VoiceChoice? value)
    {
        if (!_ready)
        {
            return;
        }

        if (value is not null)
        {
            _store.Set(CompanionOptions.SectionName, nameof(CompanionOptions.Voice), value.Id);
        }
    }

    partial void OnSpeedChanged(double value)
    {
        if (_ready)
        {
            _store.Set(CompanionOptions.SectionName, nameof(CompanionOptions.Speed), Math.Round(value, 2));
        }
    }

    partial void OnCaptionSecondsChanged(double value)
    {
        if (_ready)
        {
            _store.Set(CompanionOptions.SectionName, nameof(CompanionOptions.CaptionSeconds), Math.Round(value));
        }
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> Speak(string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var style = new SpeechStyle(SelectedVoice?.Id ?? "af_heart", Speed);
        await foreach (var chunk in _speech.SynthesizeAsync(text, style, cancellationToken))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// The English Kokoro voices shipped next to the app. Names encode accent and gender:
    /// "af_heart" is American, female, "Heart".
    /// </summary>
    private static List<VoiceChoice> AvailableVoices()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "voices");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(folder, "*.npy")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(id => id.Length > 3 && id[0] is 'a' or 'b' && id[2] == '_')
            .Order(StringComparer.Ordinal)
            .Select(id => new VoiceChoice(id, Describe(id)))];
    }

    private static string Describe(string id)
    {
        var name = char.ToUpperInvariant(id[3]) + id[4..];
        var accent = id[0] == 'a' ? "American" : "British";
        var gender = id[1] == 'f' ? "female" : "male";
        return $"{name} ({accent}, {gender})";
    }
}

public sealed record VoiceChoice(string Id, string Label)
{
    public override string ToString() => Label;
}
