using System.Diagnostics;
using Jarvis.Core.Assistant;

namespace Jarvis.Platform.Mac.Shell;

/// <summary>
/// "Open Spotify", media keys and volume on a Mac. Apps are the .app bundles in the usual
/// folders (refreshed every 10 minutes); media keys and volume go through AppleScript
/// (osascript), which every Mac has.
/// </summary>
public sealed class MacApps : IApps
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    private static readonly string[] Folders =
    [
        "/Applications",
        "/Applications/Utilities",
        "/System/Applications",
        "/System/Applications/Utilities",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
    ];

    private readonly Lock _gate = new();
    private IReadOnlyList<InstalledApp> _apps = [];
    private DateTime _readAt = DateTime.MinValue;

    public IReadOnlyList<InstalledApp> All
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _readAt > Fresh)
                {
                    _apps = Folders.Where(Directory.Exists)
                        .SelectMany(folder => Directory.EnumerateDirectories(folder, "*.app"))
                        .Select(bundle => new InstalledApp(Path.GetFileNameWithoutExtension(bundle), bundle))
                        .DistinctBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    _readAt = DateTime.UtcNow;
                }

                return _apps;
            }
        }
    }

    /// <summary>Media keys as AppleScript for the Music app and system volume.</summary>
    private static readonly Dictionary<string, string> MediaScripts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playpause"] = "tell application \"Music\" to playpause",
        ["play"] = "tell application \"Music\" to play",
        ["pause"] = "tell application \"Music\" to pause",
        ["next"] = "tell application \"Music\" to next track",
        ["previous"] = "tell application \"Music\" to previous track",
        ["stop"] = "tell application \"Music\" to stop",
        ["volumeup"] = "set volume output volume ((output volume of (get volume settings)) + 10)",
        ["volumedown"] = "set volume output volume ((output volume of (get volume settings)) - 10)",
        ["mute"] = "set volume output muted (not (output muted of (get volume settings)))",
    };

    public bool PressKey(string key)
        => MediaScripts.TryGetValue(key.Replace(" ", string.Empty, StringComparison.Ordinal), out var script) && AppleScript(script);

    public void EnsureVolume(int percent)
        => AppleScript($"set volume without output muted\nif output volume of (get volume settings) < {Math.Clamp(percent, 0, 100)} then set volume output volume {Math.Clamp(percent, 0, 100)}");

    public bool Launch(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return Directory.Exists(app.LaunchId) && app.LaunchId.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Native.OpenUrl(app.LaunchId) == 1;
    }

    public bool Open(string uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        // Only links and registered deep links (scheme:...), never a program path or a command.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.IsFile || parsed.IsUnc)
        {
            return false;
        }

        return Native.OpenUrl(TranslateSettings(parsed.OriginalString)) == 1;
    }

    /// <summary>Windows Settings deep links Core knows, as the nearest System Settings pane.</summary>
    private static string TranslateSettings(string uri) => uri.ToLowerInvariant() switch
    {
        var u when u.StartsWith("ms-settings:privacy-microphone", StringComparison.Ordinal) => "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone",
        var u when u.StartsWith("ms-settings:bluetooth", StringComparison.Ordinal) => "x-apple.systempreferences:com.apple.BluetoothSettings",
        var u when u.StartsWith("ms-settings:network", StringComparison.Ordinal) => "x-apple.systempreferences:com.apple.wifi-settings-extension",
        var u when u.StartsWith("ms-settings:display", StringComparison.Ordinal) => "x-apple.systempreferences:com.apple.Displays-Settings.extension",
        var u when u.StartsWith("ms-settings:sound", StringComparison.Ordinal) => "x-apple.systempreferences:com.apple.Sound-Settings.extension",
        var u when u.StartsWith("ms-settings:", StringComparison.Ordinal) => "x-apple.systempreferences:",
        _ => uri,
    };

    private static bool AppleScript(string script)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/osascript") { ArgumentList = { "-e", script }, UseShellExecute = false, RedirectStandardError = true });
            return process is not null && process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
