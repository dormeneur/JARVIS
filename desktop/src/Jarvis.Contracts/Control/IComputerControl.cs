namespace Jarvis.Contracts.Control;

public enum ComputerActionKind
{
    Click,
    DoubleClick,
    RightClick,
    ScrollUp,
    ScrollDown,
    Type,
    Key,

    /// <summary>Open a web link (<see cref="ComputerAction.Text"/>) in the user's browser.</summary>
    Open,
}

/// <param name="X">Desktop pixels (pointer actions only).</param>
/// <param name="Y">Desktop pixels (pointer actions only).</param>
/// <param name="Text">What to type, or the key combination ("ctrl+s").</param>
public sealed record ComputerAction(ComputerActionKind Kind, int X, int Y, string? Text);

/// <summary>Uses the real mouse and keyboard. Only ever called after the user approved the step.</summary>
public interface IComputerControl
{
    /// <returns>False if the action couldn't be done (e.g. an unknown key name).</returns>
    bool Perform(ComputerAction action);

    /// <summary>
    /// Watches for the user taking back control (moving the mouse themselves or pressing a key)
    /// until disposed; <paramref name="tookBack"/> is called once, on any thread.
    /// </summary>
    IDisposable WatchForUser(Action tookBack);
}
