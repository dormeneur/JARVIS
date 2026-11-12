namespace Jarvis.Platform.Mac.Shell;

/// <summary>Launch at login through SMAppService (macOS 13+). Only works from Jarvis.app.</summary>
public static class LoginItem
{
    /// <summary>True enabled, false off, null unavailable here (not an .app) or waiting for approval in System Settings.</summary>
    public static bool? Enabled => Native.LoginStatus() switch
    {
        1 => true,
        0 => false,
        _ => null,
    };

    public static bool Set(bool enabled) => Native.LoginSet(enabled ? 1 : 0) == 1;
}
