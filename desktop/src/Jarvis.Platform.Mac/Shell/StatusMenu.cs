using System.Runtime.InteropServices;
using System.Text.Json;

namespace Jarvis.Platform.Mac.Shell;

/// <summary>A menu bar entry: a command, a separator (null label), or a checked toggle.</summary>
public sealed record MenuEntry(string? Label, Action? Invoke = null, bool Enabled = true, bool Checked = false, int Indent = 0)
{
    public static MenuEntry Separator { get; } = new((string?)null);
}

/// <summary>
/// The menu bar item (the Mac's tray icon). The menu is rebuilt from <paramref name="build"/>
/// each time it opens, so it's always current. Create it on the main thread.
/// </summary>
public sealed unsafe class StatusMenu
{
    private static StatusMenu? _current;
    private readonly Func<IReadOnlyList<MenuEntry>> _build;
    private IReadOnlyList<MenuEntry> _shown = [];

    public StatusMenu(Func<IReadOnlyList<MenuEntry>> build)
    {
        _build = build;
        _current = this;
        Native.StatusCreate(&OnWillOpen, &OnClicked);
    }

    public string? Tooltip
    {
        set => Native.StatusSetTooltip(value);
    }

    public bool Dimmed
    {
        set => Native.StatusSetDimmed(value ? 1 : 0);
    }

    private void Refresh()
    {
        _shown = _build();
        var items = _shown.Select((entry, index) => entry.Label is null
            ? new Dictionary<string, object> { ["separator"] = true }
            : new Dictionary<string, object> { ["id"] = index, ["title"] = entry.Label, ["enabled"] = entry.Enabled && entry.Invoke is not null, ["checked"] = entry.Checked, ["indent"] = entry.Indent });
        Native.StatusSetMenu(JsonSerializer.Serialize(items));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnWillOpen() => MainThread.Guard(() => _current?.Refresh());

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnClicked(int id) => MainThread.Guard(() =>
    {
        if (_current is { } menu && id >= 0 && id < menu._shown.Count)
        {
            menu._shown[id].Invoke?.Invoke();
        }
    });
}
