namespace Jarvis.Core;

/// <summary>
/// Paused Jarvis stays in the tray but is otherwise asleep: shortcuts are ignored and the cursor and
/// notch are hidden. Thread-safe; <see cref="Changed"/> is raised on the caller's thread.
/// </summary>
public sealed class PauseSwitch
{
    private volatile bool _isPaused;

    public event Action<bool>? Changed;

    public bool IsPaused => _isPaused;

    public void Set(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        Changed?.Invoke(paused);
    }
}
