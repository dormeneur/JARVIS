namespace Jarvis.Contracts.Audio;

/// <summary>
/// The OS refused the microphone: Windows' privacy settings block desktop apps (Settings → Privacy &amp;
/// security → Microphone → "Let desktop apps access your microphone"), or macOS hasn't allowed
/// Jarvis (System Settings → Privacy &amp; Security → Microphone).
/// </summary>
public sealed class MicrophoneBlockedException : Exception
{
    /// <summary>The settings page with the switch.</summary>
    public static string SettingsPage => OperatingSystem.IsMacOS()
        ? "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"
        : "ms-settings:privacy-microphone";

    /// <summary>What to tell the user, and the button that opens <see cref="SettingsPage"/>.</summary>
    public static string Advice => OperatingSystem.IsMacOS()
        ? "macOS is blocking Jarvis from the microphone. Allow Jarvis in System Settings → Privacy & Security → Microphone, then try again."
        : "Windows is blocking Jarvis from the microphone. Turn on \"Let desktop apps access your microphone\" in Windows settings, then try again.";

    public static string SettingsLabel => OperatingSystem.IsMacOS() ? "Open System Settings" : "Open Windows settings";

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
