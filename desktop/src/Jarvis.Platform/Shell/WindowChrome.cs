using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.System.WinRT;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Jarvis.Platform.Shell;

/// <summary>Win32 window tweaks the XAML/AppWindow APIs don't expose.</summary>
public static class WindowChrome
{
    /// <summary>Set to 1 to let screenshots and recordings see Jarvis's floating surfaces.</summary>
    public const string CapturableEnvironmentVariable = "JARVIS_OVERLAY_CAPTURABLE";

    private const uint DwmColorNone = 0xFFFFFFFE;
    private const nuint SubclassId = 0x4A41;
    private static readonly HWND TopmostBand = new(-1);

    // Kept in a static field so the delegate outlives every window it is installed on.
    private static readonly SUBCLASSPROC FloatingSurfaceProcedure = OnFloatingSurfaceMessage;

    /// <summary>The "Show in screen recordings" setting, applied at start-up before any window exists.</summary>
    public static bool ShowInRecordings { get; set; }

    public static bool ExcludeFromCapture => !ShowInRecordings && Environment.GetEnvironmentVariable(CapturableEnvironmentVariable) != "1";

    /// <summary>
    /// Call after the window is shown: AppWindow.Show re-applies its presenter's frame styles.
    /// Makes a window a floating surface: mouse input falls through to whatever is underneath, it
    /// never takes focus, stays off Alt+Tab, and (unless debugging) is invisible to screen capture.
    /// </summary>
    public static unsafe void MakeFloatingSurface(nint hwnd)
    {
        var window = new HWND(hwnd);

        // A plain popup: no caption, sizing frame or system menu (they leave a 1 px frame behind).
        var style = (WINDOW_STYLE)PInvoke.GetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        style = (style & ~WINDOW_STYLE.WS_OVERLAPPEDWINDOW) | WINDOW_STYLE.WS_POPUP;
        PInvoke.SetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)style);

        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);

        // A layered window shows nothing until it has attributes; full opacity keeps XAML content as drawn.
        PInvoke.SetLayeredWindowAttributes(window, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

        // Windows 11 rounds and outlines top-level windows; a floating surface draws its own shape.
        var corner = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
        var noBorder = DwmColorNone;
        PInvoke.DwmSetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &noBorder, sizeof(uint));
        var noTransitions = 1;
        PInvoke.DwmSetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &noTransitions, sizeof(int));

        // "Sheet of glass": the whole client area is composed with alpha, so transparent pixels show the desktop.
        var glass = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        PInvoke.DwmExtendFrameIntoClientArea(window, glass);
        PInvoke.SetWindowPos(window, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER
                | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED);

        PInvoke.SetWindowSubclass(window, FloatingSurfaceProcedure, SubclassId, 0);

        if (ExcludeFromCapture && !PInvoke.SetWindowDisplayAffinity(window, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
        {
            PInvoke.SetWindowDisplayAffinity(window, WINDOW_DISPLAY_AFFINITY.WDA_MONITOR);
        }
    }

    /// <summary>
    /// Lets a floating surface take clicks and keyboard focus (to type into it), or makes it
    /// click-through and focus-proof again.
    /// </summary>
    public static void SetInteractive(nint hwnd, bool interactive)
    {
        var window = new HWND(hwnd);
        const WINDOW_EX_STYLE passive = WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle = interactive ? exStyle & ~passive : exStyle | passive;
        PInvoke.SetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);
    }

    public static nint ForegroundWindow() => PInvoke.GetForegroundWindow();

    /// <summary>
    /// Takes clicks only inside <paramref name="area"/> (client pixels), still without taking focus;
    /// <see langword="null"/> makes the whole window click-through again. Nothing outside the area is
    /// drawn while it is set, so it must cover all the visible content.
    /// </summary>
    public static void SetClickableArea(nint hwnd, System.Drawing.Rectangle? area)
    {
        var window = new HWND(hwnd);
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle = area is null ? exStyle | WINDOW_EX_STYLE.WS_EX_TRANSPARENT : exStyle & ~WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
        PInvoke.SetWindowLongPtr(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)exStyle);

        // The window owns the region once set; Windows frees it.
        var region = area is { } r ? PInvoke.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom) : HRGN.Null;
        PInvoke.SetWindowRgn(window, region, true);
    }

    /// <summary>
    /// Gives a window keyboard focus even when another app is in front. Windows only lets the
    /// process that received the last input do that, so an empty mouse input comes first.
    /// </summary>
    public static void Activate(nint hwnd)
    {
        Span<INPUT> nothing = [new INPUT { type = INPUT_TYPE.INPUT_MOUSE }];
        PInvoke.SendInput(nothing, Marshal.SizeOf<INPUT>());
        PInvoke.SetForegroundWindow(new HWND(hwnd));
    }

    /// <summary>Display scale (1.0 = 96 DPI) of the monitor containing a desktop point.</summary>
    public static double DisplayScaleAt(int x, int y)
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(x, y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, Windows.Win32.UI.HiDpi.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;
    }

    /// <summary>
    /// Puts the window back at the top of the always-on-top band. Other topmost windows that
    /// activate later go above it, so call this whenever the surface has something new to show.
    /// </summary>
    public static void RaiseToTop(nint hwnd)
        => PInvoke.SetWindowPos(new HWND(hwnd), TopmostBand, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    private static unsafe LRESULT OnFloatingSurfaceMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        switch (message)
        {
            // GDI's default erase paints the window white, which shows as a hairline around the XAML
            // content. Black on a glass frame is fully transparent.
            case PInvoke.WM_ERASEBKGND:
                PInvoke.GetClientRect(hwnd, out var bounds);
                var black = new HBRUSH(PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.BLACK_BRUSH).Value);
                PInvoke.FillRect(new HDC((void*)wParam.Value), &bounds, black);
                return new LRESULT(1);

            // No non-client area at all: the client rectangle is the whole window, so no frame can be
            // drawn even if the windowing layer re-adds caption/frame styles later (AppWindow does).
            case PInvoke.WM_NCCALCSIZE when wParam.Value != 0:
                return new LRESULT(0);

            case PInvoke.WM_MOUSEACTIVATE when IsPassive(hwnd):
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);

            default:
                return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
        }
    }

    private static bool IsPassive(HWND hwnd)
        => ((WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_NOACTIVATE);

    /// <summary>
    /// The system (Windows.UI) compositor needs a Windows.System dispatcher queue on the calling
    /// thread; WinUI threads only have the Microsoft.UI one. Creates it once, if missing.
    /// </summary>
    public static void EnsureSystemDispatcherQueue()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return;
        }

        var options = new DispatcherQueueOptions
        {
            dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
            apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
        };
        PInvoke.CreateDispatcherQueueController(options, out _).ThrowOnFailure();
    }
}
