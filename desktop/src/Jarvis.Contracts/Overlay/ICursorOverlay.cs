namespace Jarvis.Contracts.Overlay;

/// <summary>
/// The floating companion cursor. Thread-safe: calls are queued to the overlay's render thread.
/// Coordinates are physical pixels on the virtual desktop.
/// </summary>
public interface ICursorOverlay
{
    /// <summary>Shows the thinking dots around the cursor while a turn is in progress.</summary>
    /// <summary>
    /// The user clicked the cursor while a mouse shake had left it behind. Raised on a worker thread.
    /// </summary>
    event Action? Clicked;

    void SetThinking(bool thinking);

    /// <summary>The glowing border around every screen that says "Jarvis has the mouse and keyboard".</summary>
    void ShowControlBorder(bool on);

    /// <summary>What the user circled or held the mouse on during the last listen, once; then <see langword="null"/>.</summary>
    ScreenHighlight? TakeHighlight();

    /// <summary>Shows the listening meter in place of the cursor while <paramref name="listening"/>.</summary>
    void SetListening(bool listening);

    /// <summary>The microphone's current 0–1 level for the meter; ignored when not listening.</summary>
    void ShowLevel(double level);

    /// <summary>Flies the cursor to a point and keeps it there until the user moves the mouse away.</summary>
    /// <param name="label">What it is ("Settings"), shown in a bubble on arrival; a short phrase when empty.</param>
    void PointAt(int x, int y, string? label = null);

    /// <summary>Abandons pointing and resumes trailing the mouse.</summary>
    void ReturnToMouse();
}
