using System.Runtime.InteropServices;
using Vortice.DirectComposition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// A transparent, click-through, never-focused window covering one monitor. Content is presented
/// through DirectComposition, so there is no GDI surface and no per-pixel cost for the empty area.
/// </summary>
internal sealed unsafe class OverlayWindow : IDisposable
{
    private const string ClassName = "JarvisOverlayWindow";
    private static readonly WNDPROC WindowProcedure = OnMessage;
    private static bool _classRegistered;

    private readonly HWND _hwnd;
    private readonly IDCompositionTarget _target;

    public OverlayWindow(Display display, IDCompositionDevice device, bool excludeFromCapture)
    {
        Display = display;
        RegisterClassOnce();

        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            ClassName,
            "Jarvis Overlay",
            WINDOW_STYLE.WS_POPUP,
            display.Left,
            display.Top,
            display.Width,
            display.Height,
            HWND.Null,
            null,
            null,
            null);
        if (_hwnd.IsNull)
        {
            throw new InvalidOperationException($"CreateWindowEx failed for the overlay on {display}.");
        }

        PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        if (excludeFromCapture
            && !PInvoke.SetWindowDisplayAffinity(_hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
        {
            PInvoke.SetWindowDisplayAffinity(_hwnd, WINDOW_DISPLAY_AFFINITY.WDA_MONITOR);
        }

        device.CreateTargetForHwnd(_hwnd, true, out _target).CheckError();
        Root = device.CreateVisual();
        _target.SetRoot(Root);
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    /// <summary>Raised on the overlay thread when monitors or scaling change.</summary>
    public static event Action? DisplaysChanged;

    public Display Display { get; }

    /// <summary>Container for this monitor's content; children are positioned relative to the monitor.</summary>
    public IDCompositionVisual Root { get; }

    /// <summary>
    /// Back above every other always-on-top window, the taskbar included; they re-raise themselves,
    /// so do this whenever the overlay is about to show something.
    /// </summary>
    public void RaiseToTop() => Jarvis.Platform.Shell.WindowChrome.RaiseToTop(_hwnd);

    public void Dispose()
    {
        Root.Dispose();
        _target.Dispose();
        PInvoke.DestroyWindow(_hwnd);
    }

    private static void RegisterClassOnce()
    {
        if (_classRegistered)
        {
            return;
        }

        fixed (char* className = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = WindowProcedure,
                hInstance = PInvoke.GetModuleHandle((PCWSTR)null),
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(windowClass) == 0)
            {
                throw new InvalidOperationException("RegisterClassEx failed for the overlay window class.");
            }
        }

        _classRegistered = true;
    }

    private static LRESULT OnMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message is PInvoke.WM_DISPLAYCHANGE or PInvoke.WM_DPICHANGED)
        {
            DisplaysChanged?.Invoke();
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
