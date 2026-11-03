using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Jarvis.Platform.Shell;

/// <summary>
/// A notification-area icon with a right-click menu. Create it on the UI thread: menu actions run
/// on whichever thread pumps its hidden window's messages.
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const string ClassName = "JarvisTrayWindow";
    private const uint CallbackMessage = PInvoke.WM_APP + 1;
    private const uint IconId = 1;

    private static readonly WNDPROC WindowProcedure = OnMessage;
    private static readonly Dictionary<HWND, TrayIcon> Instances = [];
    private static uint _taskbarCreatedMessage;

    private readonly HWND _hwnd;
    private readonly Func<IReadOnlyList<TrayMenuItem>> _menu;
    private string _tooltip;
    private HICON _icon;

    /// <param name="tooltip">Shown on hover; at most 127 characters.</param>
    /// <param name="iconFile">An .ico file; the generic Windows icon if it can't be loaded.</param>
    /// <param name="menu">Built each time the menu opens, so labels can reflect current state.</param>
    public TrayIcon(string tooltip, string iconFile, Func<IReadOnlyList<TrayMenuItem>> menu)
    {
        _tooltip = tooltip;
        _icon = LoadIcon(iconFile);
        _menu = menu;
        RegisterClassOnce();
        _hwnd = PInvoke.CreateWindowEx(0, ClassName, ClassName, 0, 0, 0, 0, 0, HWND.Null, null, null, null);
        Instances[_hwnd] = this;
        AddIcon();
    }

    /// <summary>Swaps the icon and tooltip in place (e.g. greyed out while paused).</summary>
    public void Update(string tooltip, string iconFile)
    {
        var previous = _icon;
        _tooltip = tooltip;
        _icon = LoadIcon(iconFile);
        var data = NewData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, data);
        PInvoke.DestroyIcon(previous); // fails harmlessly for the shared system icon
    }

    public void Dispose()
    {
        var data = NewData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, data);
        Instances.Remove(_hwnd);
        PInvoke.DestroyWindow(_hwnd);
        PInvoke.DestroyIcon(_icon);
    }

    private void AddIcon()
    {
        var data = NewData();
        data.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tooltip;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, data);
    }

    /// <summary>The file's icon at the tray's size for the system DPI.</summary>
    private static HICON LoadIcon(string file)
    {
        var size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
        fixed (char* path = file)
        {
            var icon = PInvoke.LoadImage(HINSTANCE.Null, path, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_LOADFROMFILE);
            return icon.IsNull ? PInvoke.LoadIcon(HINSTANCE.Null, PInvoke.IDI_APPLICATION) : new HICON(icon.Value);
        }
    }

    private NOTIFYICONDATAW NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
    };

    private void ShowMenu()
    {
        var items = _menu();
        var menu = PInvoke.CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var flags = items[i].IsSeparator ? MENU_ITEM_FLAGS.MF_SEPARATOR : MENU_ITEM_FLAGS.MF_STRING;
                fixed (char* label = items[i].Label)
                {
                    PInvoke.AppendMenu(menu, flags, (nuint)(i + 1), label);
                }
            }

            PInvoke.GetCursorPos(out var point);
            // Required so the menu closes when the user clicks elsewhere.
            PInvoke.SetForegroundWindow(_hwnd);
            var chosen = PInvoke.TrackPopupMenu(
                menu,
                TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN,
                point.X,
                point.Y,
                0,
                _hwnd,
                null);
            if (chosen.Value > 0)
            {
                items[chosen.Value - 1].Invoke?.Invoke();
            }
        }
        finally
        {
            PInvoke.DestroyMenu(menu);
        }
    }

    private static void RegisterClassOnce()
    {
        if (_taskbarCreatedMessage != 0)
        {
            return;
        }

        _taskbarCreatedMessage = PInvoke.RegisterWindowMessage("TaskbarCreated");
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
                throw new InvalidOperationException("RegisterClassEx failed for the tray window class.");
            }
        }
    }

    private static LRESULT OnMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (Instances.TryGetValue(hwnd, out var icon))
        {
            if (message == CallbackMessage && (uint)lParam.Value is PInvoke.WM_RBUTTONUP or PInvoke.WM_LBUTTONUP)
            {
                icon.ShowMenu();
                return new LRESULT(0);
            }

            // Explorer restarted: the notification area is new and our icon is gone.
            if (message == _taskbarCreatedMessage)
            {
                icon.AddIcon();
            }
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}

/// <summary>A tray menu entry; use <see cref="Separator"/> for a divider line.</summary>
public sealed record TrayMenuItem(string Label, Action? Invoke)
{
    public static TrayMenuItem Separator { get; } = new(string.Empty, null);

    public bool IsSeparator => Invoke is null && Label.Length == 0;
}
