using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Jarvis.Platform.Overlay;

/// <summary>
/// A 44-DIP square over the left-behind cursor that takes clicks (DESIGN-FACTS.md §3). The overlay
/// itself is click-through, so this is the only thing there to hit. Nearly transparent: windows
/// with zero alpha don't receive clicks. Never takes focus. Overlay thread only.
/// </summary>
internal sealed unsafe class ClickCatcher : IDisposable
{
    private const string ClassName = "JarvisClickCatcher";
    private const double SizeDips = 44;
    private static readonly WNDPROC WindowProcedure = OnMessage;
    private static readonly HWND TopmostBand = new(-1);
    private static bool _classRegistered;
    private static bool _clicked;

    private readonly HWND _hwnd;
    private bool _visible;

    public ClickCatcher(bool excludeFromCapture)
    {
        RegisterClassOnce();
        _hwnd = PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            ClassName,
            "Jarvis Cursor",
            WINDOW_STYLE.WS_POPUP,
            0,
            0,
            1,
            1,
            HWND.Null,
            null,
            null,
            null);
        PInvoke.SetLayeredWindowAttributes(_hwnd, new COLORREF(0), 1, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        if (excludeFromCapture)
        {
            PInvoke.SetWindowDisplayAffinity(_hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE);
        }
    }

    /// <summary>Centres the target on a desktop point (physical pixels) and shows it.</summary>
    public void ShowAt(double x, double y, double scale)
    {
        var size = (int)Math.Round(SizeDips * scale);
        PInvoke.SetWindowPos(_hwnd, TopmostBand, (int)(x - (size / 2.0)), (int)(y - (size / 2.0)), size, size,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        _visible = true;
    }

    public void Hide()
    {
        if (_visible)
        {
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            _visible = false;
        }
    }

    /// <summary>True once per click since the last call.</summary>
    public static bool TakeClick()
    {
        var clicked = _clicked;
        _clicked = false;
        return clicked;
    }

    public void Dispose() => PInvoke.DestroyWindow(_hwnd);

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
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_HAND),
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(windowClass) == 0)
            {
                throw new InvalidOperationException("RegisterClassEx failed for the click catcher.");
            }
        }

        _classRegistered = true;
    }

    private static LRESULT OnMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_LBUTTONDOWN:
                _clicked = true;
                return new LRESULT(0);
            case PInvoke.WM_MOUSEACTIVATE:
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);
            default:
                return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
}
