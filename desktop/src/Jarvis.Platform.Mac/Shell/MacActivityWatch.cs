using System.Text.Json;
using Jarvis.Contracts.Activity;

namespace Jarvis.Platform.Mac.Shell;

/// <summary>
/// The app in front and its window title (window titles need Accessibility), how long the user has
/// been idle, and whether something fullscreen is showing. Never Jarvis itself, nor private
/// browser windows.
/// </summary>
public sealed class MacActivityWatch : IActivityWatch
{
    private static readonly string[] PrivateWindows = ["InPrivate", "Incognito", "Private Browsing", "Private browsing"];

    public bool InFullscreen => Front() is { Fullscreen: true };

    public ForegroundWindow? Foreground()
    {
        if (Front() is not { } front || front.Pid == Environment.ProcessId || front.Title.Length == 0
            || PrivateWindows.Any(marker => front.Title.Contains(marker, StringComparison.Ordinal)))
        {
            return null;
        }

        return new ForegroundWindow(front.App, front.Title);
    }

    public TimeSpan IdleFor() => TimeSpan.FromSeconds(Native.IdleSeconds());

    /// <summary>The frontmost app, or null when there is none.</summary>
    public static FrontApp? Front()
    {
        if (Native.Take(Native.FrontApp()) is not { } json)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new FrontApp(
            root.GetProperty("app").GetString() ?? string.Empty,
            root.GetProperty("bundle").GetString() ?? string.Empty,
            root.GetProperty("pid").GetInt32(),
            root.GetProperty("title").GetString() ?? string.Empty,
            root.GetProperty("fullscreen").GetBoolean());
    }
}

public sealed record FrontApp(string App, string Bundle, int Pid, string Title, bool Fullscreen);
