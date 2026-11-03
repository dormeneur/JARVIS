namespace Jarvis.Core.Providers;

/// <summary>
/// Which engine serves each AI capability. Bound from the "Providers" settings section; each value
/// is the key the engine was registered under (see <see cref="ProviderRegistration"/>).
/// </summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    public string SpeechToText { get; set; } = ProviderKeys.Whisper;

    public string TextToSpeech { get; set; } = ProviderKeys.Kokoro;

    public string Brain { get; set; } = ProviderKeys.ClaudeCli;
}

/// <summary>Registration keys for the engines that ship with the app.</summary>
public static class ProviderKeys
{
    public const string Whisper = "whisper";
    public const string Kokoro = "kokoro";
    public const string WindowsSpeech = "windows";
    public const string ClaudeCli = "claude-cli";
}
