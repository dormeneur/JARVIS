using System.Text.Json.Nodes;
using Jarvis.Core.Dictation;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Hosting;

/// <summary>Keeps dictionary words in settings.json under Dictation:Words, newest last.</summary>
internal sealed class SettingsVocabularyStore : IVocabularyStore
{
    private readonly IOptionsMonitor<DictationOptions> _options;
    private readonly Lock _gate = new();

    public SettingsVocabularyStore(IOptionsMonitor<DictationOptions> options)
    {
        _options = options;
    }

    public bool Add(string word)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(word);
        lock (_gate)
        {
            var words = _options.CurrentValue.Words;
            if (words.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var kept = words.Append(word).TakeLast(DictationOptions.MaximumWords);
            SettingsFile.Set(DictationOptions.SectionName, nameof(DictationOptions.Words), new JsonArray([.. kept.Select(w => JsonValue.Create(w))]));
            return true;
        }
    }
}
