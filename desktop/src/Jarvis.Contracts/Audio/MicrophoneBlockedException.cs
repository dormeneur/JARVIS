namespace Jarvis.Contracts.Audio;

/// <summary>
/// Windows refused the microphone: its privacy settings block desktop apps (Settings → Privacy &amp;
/// security → Microphone → "Let desktop apps access your microphone").
/// </summary>
public sealed class MicrophoneBlockedException : Exception
{
    /// <summary>The Windows settings page with the switch.</summary>
    public const string SettingsPage = "ms-settings:privacy-microphone";

    public MicrophoneBlockedException()
        : base("Windows is blocking desktop apps from using the microphone.")
    {
    }

    public MicrophoneBlockedException(string message)
        : base(message)
    {
    }

    public MicrophoneBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
