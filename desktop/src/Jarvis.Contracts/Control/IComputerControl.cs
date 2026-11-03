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

    // Steps in the browser, done through the extension on a page element or tab (not the mouse).

    /// <summary>Press a button or link on the page.</summary>
    Press,

    /// <summary>Type text into a field on the page.</summary>
    Fill,

    /// <summary>Copy an element's text (or the selection) to Jarvis's clipboard.</summary>
    Copy,

    /// <summary>Paste what Jarvis copied into a field, in any tab.</summary>
    Paste,

    /// <summary>Scroll the page: up, down, top or bottom.</summary>
    PageScroll,

    /// <summary>Bring a tab to the front.</summary>
    TabSwitch,

    /// <summary>Close a tab.</summary>
    TabClose,
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
