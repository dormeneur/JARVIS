namespace Jarvis.Contracts.Activity;

/// <summary>What the user is doing right now, for the proactive layer. Cheap to call.</summary>
public interface IActivityWatch
{
    /// <summary>The app and window in front; null when it's Jarvis, the desktop or a private browser window.</summary>
    ForegroundWindow? Foreground();

    /// <summary>How long since the user last touched the mouse or keyboard.</summary>
    TimeSpan IdleFor();

    /// <summary>Something fullscreen is in front (a game, a video, a presentation): don't interrupt.</summary>
    bool InFullscreen { get; }
}

/// <param name="App">The program's name, e.g. "Code" or "chrome".</param>
/// <param name="Title">The window's title.</param>
public sealed record ForegroundWindow(string App, string Title);
