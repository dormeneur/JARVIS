namespace Jarvis.Core.Companion;

/// <summary>Voice settings, bound from the "Voice" settings section.</summary>
public sealed class CompanionOptions
{
    public const string SectionName = "Voice";

    /// <summary>Engine-specific voice id (Kokoro: e.g. af_heart, am_michael, bf_emma).</summary>
    public string Voice { get; set; } = "af_heart";

    /// <summary>1.0 = normal.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>BCP-47 code for speech recognition, or empty to auto-detect.</summary>
    public string Language { get; set; } = "en";

    /// <summary>How long the written answer stays in the notch after speaking ends.</summary>
    public double CaptionSeconds { get; set; } = 8;
}
