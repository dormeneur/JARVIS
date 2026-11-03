namespace Jarvis.Platform.Audio;

/// <summary>Which microphone to record from, bound from the "Microphone" settings section.</summary>
public sealed class MicrophoneOptions
{
    public const string SectionName = "Microphone";

    /// <summary>A Windows audio endpoint ID, or empty for the Windows default.</summary>
    public string DeviceId { get; set; } = string.Empty;
}
