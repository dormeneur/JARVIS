namespace Jarvis.Core.Dictation;

/// <summary>Dictation settings, bound from the "Dictation" settings section.</summary>
public sealed class DictationOptions
{
    public const string SectionName = "Dictation";

    /// <summary>The most custom words kept; more than this dilutes the recogniser's hints.</summary>
    public const int MaximumWords = 50;

    /// <summary>Let a fast model fix punctuation and remove filler words before typing.</summary>
    public bool Cleanup { get; set; } = true;

    /// <summary>Model used for clean-up; small and fast is right for this job.</summary>
    public string CleanupModel { get; set; } = "haiku";

    /// <summary>BCP-47 code for speech recognition, or empty to auto-detect.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Names and terms to favour when recognising and cleaning up.</summary>
    public List<string> Words { get; set; } = [];

    /// <summary>How long the "copied, press Ctrl+V" note stays when text could not be placed.</summary>
    public double NoticeSeconds { get; set; } = 4;
}
