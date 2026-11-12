using System.Runtime.InteropServices;
using System.Text.Json;

namespace Jarvis.Platform.Mac.Shell;

/// <summary>
/// The native Settings window. The app describes its pages (any JSON-serialisable rows, see
/// Settings.swift) and gets each change as a key and a JSON value, and each button as its id.
/// </summary>
public static unsafe class SettingsWindow
{
    private static Action<string, JsonElement>? _changed;
    private static Action<int>? _clicked;

    public static void Start(Action<string, JsonElement> changed, Action<int> clicked)
    {
        _changed = changed;
        _clicked = clicked;
        Native.SettingsStart(&OnAction, &OnText);
    }

    /// <summary>Sends the pages; with <paramref name="show"/> the window opens (on <paramref name="section"/>).</summary>
    public static void Update(object pages, string? banner, bool show, string? section)
        => Native.SettingsUpdate(JsonSerializer.Serialize(pages), banner, show ? 1 : 0, section);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnAction(int id) => MainThread.Guard(() => _clicked?.Invoke(id));

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnText(int kind, byte* text) => MainThread.Guard(() =>
    {
        using var change = JsonDocument.Parse(Marshal.PtrToStringUTF8((nint)text) ?? "{}");
        if (kind == 2 && change.RootElement.TryGetProperty("key", out var key) && change.RootElement.TryGetProperty("value", out var value))
        {
            _changed?.Invoke(key.GetString() ?? string.Empty, value.Clone());
        }
    });
}
