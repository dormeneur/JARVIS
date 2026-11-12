using System.Runtime.InteropServices;

namespace Jarvis.Platform.Mac;

/// <summary>
/// The AppKit thread (the process's first thread, which runs the app loop). UI work posted here
/// runs in order, like WinUI's DispatcherQueue.
/// </summary>
public static unsafe class MainThread
{
    /// <summary>Runs the app loop on the calling (main) thread until the app quits; <paramref name="ready"/> runs on it first.</summary>
    public static void Run(Action ready)
    {
        _ready = ready;
        Native.AppRun(&OnReady);
    }

    public static void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Native.OnMain(&OnWork, GCHandle.ToIntPtr(GCHandle.Alloc(work)));
    }

    /// <summary>Makes <see cref="Run"/> return (from any thread).</summary>
    public static void Quit() => Native.AppQuit();

    /// <summary>Ends the process at once, skipping native static destructors (call after cleaning up).</summary>
    public static void Exit(int code) => Native.Exit(code);

    private static Action? _ready;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnReady() => Guard(_ready);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnWork(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var work = (Action?)handle.Target;
        handle.Free();
        Guard(work);
    }

    /// <summary>An exception must never unwind into native code (it would crash the app).</summary>
    internal static void Guard(Action? work)
    {
        try
        {
            work?.Invoke();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Failed?.Invoke(exception);
        }
    }

    /// <summary>Raised for an exception thrown by UI work or a native callback.</summary>
    public static event Action<Exception>? Failed;
}
