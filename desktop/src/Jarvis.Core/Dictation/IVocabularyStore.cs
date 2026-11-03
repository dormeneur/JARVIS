namespace Jarvis.Core.Dictation;

/// <summary>Where the user's dictionary words are kept (the app stores them in settings).</summary>
public interface IVocabularyStore
{
    /// <summary>
    /// Adds a word if it isn't already there (case-insensitively), dropping the oldest words beyond
    /// <see cref="DictationOptions.MaximumWords"/>. Returns <see langword="false"/> if it was already known.
    /// </summary>
    bool Add(string word);
}
