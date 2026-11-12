using System.Runtime.InteropServices;

namespace Jarvis.Platform.Mac;

/// <summary>
/// The C ABI of libjarvis_mac.dylib (desktop/native/JarvisMac). Strings go in as UTF-8; strings
/// coming back are malloc'd and must be freed with <see cref="Free"/> (use <see cref="Take"/>).
/// </summary>
internal static unsafe partial class Native
{
    private const string Library = "jarvis_mac";

    /// <summary>Copies a returned UTF-8 string and frees the native one.</summary>
    public static string? Take(nint text)
    {
        if (text == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(text);
        }
        finally
        {
            Free(text);
        }
    }

    [LibraryImport(Library, EntryPoint = "jv_free")]
    public static partial void Free(nint pointer);

    // App
    [LibraryImport(Library, EntryPoint = "jv_app_run")]
    public static partial void AppRun(delegate* unmanaged[Cdecl]<void> ready);

    [LibraryImport(Library, EntryPoint = "jv_app_quit")]
    public static partial void AppQuit();

    [LibraryImport(Library, EntryPoint = "jv_exit")]
    public static partial void Exit(int code);

    [LibraryImport(Library, EntryPoint = "jv_on_main")]
    public static partial void OnMain(delegate* unmanaged[Cdecl]<nint, void> work, nint context);

    [LibraryImport(Library, EntryPoint = "jv_open_url", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int OpenUrl(string url);

    // Menu bar
    [LibraryImport(Library, EntryPoint = "jv_status_create")]
    public static partial void StatusCreate(delegate* unmanaged[Cdecl]<void> willOpen, delegate* unmanaged[Cdecl]<int, void> clicked);

    [LibraryImport(Library, EntryPoint = "jv_status_set_menu", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void StatusSetMenu(string json);

    [LibraryImport(Library, EntryPoint = "jv_status_set_tooltip", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void StatusSetTooltip(string? text);

    [LibraryImport(Library, EntryPoint = "jv_status_set_dimmed")]
    public static partial void StatusSetDimmed(int dimmed);

    // Audio
    [LibraryImport(Library, EntryPoint = "jv_mic_access")]
    public static partial int MicAccess(int ask);

    [LibraryImport(Library, EntryPoint = "jv_mic_start", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint MicStart(int rate, int channels, string? deviceUid, delegate* unmanaged[Cdecl]<nint, byte*, int, void> chunk, nint context, out int error);

    [LibraryImport(Library, EntryPoint = "jv_mic_stop")]
    public static partial void MicStop(nint handle);

    [LibraryImport(Library, EntryPoint = "jv_mic_devices")]
    public static partial nint MicDevices();

    [LibraryImport(Library, EntryPoint = "jv_player_open")]
    public static partial nint PlayerOpen(int rate, int channels);

    [LibraryImport(Library, EntryPoint = "jv_player_write")]
    public static partial void PlayerWrite(nint handle, byte* bytes, int count);

    [LibraryImport(Library, EntryPoint = "jv_player_queued")]
    public static partial int PlayerQueued(nint handle);

    [LibraryImport(Library, EntryPoint = "jv_player_close")]
    public static partial void PlayerClose(nint handle);

    // Capture
    [LibraryImport(Library, EntryPoint = "jv_screen_access")]
    public static partial int ScreenAccess(int ask);

    [LibraryImport(Library, EntryPoint = "jv_capture")]
    public static partial int Capture(int maxEdge, double quality, delegate* unmanaged[Cdecl]<nint, byte*, int, int, int, double, double, double, double, double, void> captured, nint context);

    // Input
    [LibraryImport(Library, EntryPoint = "jv_tap_start")]
    public static partial int TapStart(delegate* unmanaged[Cdecl]<int, int, ulong, double, void> callback);

    [LibraryImport(Library, EntryPoint = "jv_input_access")]
    public static partial int InputAccess();

    [LibraryImport(Library, EntryPoint = "jv_mouse")]
    public static partial void Mouse(out double x, out double y);

    [LibraryImport(Library, EntryPoint = "jv_mouse_buttons")]
    public static partial int MouseButtons();

    [LibraryImport(Library, EntryPoint = "jv_idle_seconds")]
    public static partial double IdleSeconds();

    [LibraryImport(Library, EntryPoint = "jv_post_mouse")]
    public static partial void PostMouse(int kind, double x, double y);

    [LibraryImport(Library, EntryPoint = "jv_post_scroll")]
    public static partial void PostScroll(int lines);

    [LibraryImport(Library, EntryPoint = "jv_post_text", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void PostText(string text);

    [LibraryImport(Library, EntryPoint = "jv_post_key")]
    public static partial void PostKey(int key, ulong flags);

    [LibraryImport(Library, EntryPoint = "jv_modifiers")]
    public static partial ulong Modifiers();

    // Accessibility, pasteboard
    [LibraryImport(Library, EntryPoint = "jv_ax_trusted")]
    public static partial int AxTrusted(int prompt);

    [LibraryImport(Library, EntryPoint = "jv_ax_focused")]
    public static partial nint AxFocused(out nint app, out nint text, out int secure);

    [LibraryImport(Library, EntryPoint = "jv_ax_read")]
    public static partial nint AxRead(nint handle);

    [LibraryImport(Library, EntryPoint = "jv_ax_is_focused")]
    public static partial int AxIsFocused(nint handle);

    [LibraryImport(Library, EntryPoint = "jv_ax_release")]
    public static partial void AxRelease(nint handle);

    [LibraryImport(Library, EntryPoint = "jv_pb_save")]
    public static partial nint PasteboardSave();

    [LibraryImport(Library, EntryPoint = "jv_pb_restore")]
    public static partial void PasteboardRestore(nint handle, int restore);

    [LibraryImport(Library, EntryPoint = "jv_pb_set", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint PasteboardSet(string text, int transient);

    [LibraryImport(Library, EntryPoint = "jv_pb_change_count")]
    public static partial nint PasteboardChangeCount();

    // System
    [LibraryImport(Library, EntryPoint = "jv_screens")]
    public static partial nint Screens();

    [LibraryImport(Library, EntryPoint = "jv_front_app")]
    public static partial nint FrontApp();

    [LibraryImport(Library, EntryPoint = "jv_running_apps")]
    public static partial nint RunningApps();

    [LibraryImport(Library, EntryPoint = "jv_activate_pid")]
    public static partial int ActivatePid(int pid);

    [LibraryImport(Library, EntryPoint = "jv_kc_save", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int KeychainSave(string service, string account, string secret);

    [LibraryImport(Library, EntryPoint = "jv_kc_read", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint KeychainRead(string service, string account);

    [LibraryImport(Library, EntryPoint = "jv_kc_delete", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void KeychainDelete(string service, string account);

    [LibraryImport(Library, EntryPoint = "jv_login_status")]
    public static partial int LoginStatus();

    [LibraryImport(Library, EntryPoint = "jv_login_set")]
    public static partial int LoginSet(int enabled);

    [LibraryImport(Library, EntryPoint = "jv_spell_known", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SpellKnown(string word);

    // Overlay
    [LibraryImport(Library, EntryPoint = "jv_overlay_start")]
    public static partial void OverlayStart(delegate* unmanaged[Cdecl]<double, void> frame, delegate* unmanaged[Cdecl]<void> clicked, delegate* unmanaged[Cdecl]<void> displaysChanged, int showInRecordings);

    [LibraryImport(Library, EntryPoint = "jv_overlay_raise")]
    public static partial void OverlayRaise();

    [LibraryImport(Library, EntryPoint = "jv_cursor")]
    public static partial void Cursor(int visible, double x, double y, double rotation, double swell, uint rgb, double* dots, double* bars, double dwell);

    [LibraryImport(Library, EntryPoint = "jv_bubble", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void Bubble(string? label, double x, double y, uint rgb);

    [LibraryImport(Library, EntryPoint = "jv_catcher")]
    public static partial void Catcher(int show, double x, double y);

    [LibraryImport(Library, EntryPoint = "jv_ink_shape")]
    public static partial void InkShape(float* points, int count, uint rgb, float alpha, int kind);

    [LibraryImport(Library, EntryPoint = "jv_ink_label", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void InkLabel(string text, float x, float y, int badge, int growsLeft, float pop, uint rgb, float alpha);

    [LibraryImport(Library, EntryPoint = "jv_ink_commit")]
    public static partial void InkCommit(double x, double y);

    [LibraryImport(Library, EntryPoint = "jv_highlight")]
    public static partial void Highlight(double* points, int count, uint rgb, double alpha);

    [LibraryImport(Library, EntryPoint = "jv_border")]
    public static partial void Border(double opacity, double phase);

    // Notch
    [LibraryImport(Library, EntryPoint = "jv_notch_start")]
    public static partial void NotchStart(delegate* unmanaged[Cdecl]<int, void> action, delegate* unmanaged[Cdecl]<int, byte*, void> text);

    [LibraryImport(Library, EntryPoint = "jv_notch_hardware")]
    public static partial void NotchHardware(out double width, out double height);

    [LibraryImport(Library, EntryPoint = "jv_notch_show", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void NotchShow(string json);

    // Settings window
    [LibraryImport(Library, EntryPoint = "jv_settings_start")]
    public static partial void SettingsStart(delegate* unmanaged[Cdecl]<int, void> action, delegate* unmanaged[Cdecl]<int, byte*, void> text);

    [LibraryImport(Library, EntryPoint = "jv_settings_update", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void SettingsUpdate(string pages, string? banner, int show, string? section);

    [LibraryImport(Library, EntryPoint = "jv_debug_snapshot", StringMarshalling = StringMarshalling.Utf8)]
    public static partial void DebugSnapshot(string folder);
}
