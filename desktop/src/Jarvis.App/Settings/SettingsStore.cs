using System.Text.Json.Nodes;
using Jarvis.App.Hosting;

namespace Jarvis.App.Settings;

/// <summary>
/// The one way settings pages save a value. Writes to settings.json; the configuration system
/// reloads it, so every <c>IOptionsMonitor</c> consumer sees the change moments later.
/// </summary>
public sealed class SettingsStore
{
    public void Set(string section, string key, bool value) => SettingsFile.Set(section, key, JsonValue.Create(value));

    public void Set(string section, string key, double value) => SettingsFile.Set(section, key, JsonValue.Create(value));

    public void Set(string section, string key, int value) => SettingsFile.Set(section, key, JsonValue.Create(value));

    public void Set(string section, string key, string value) => SettingsFile.Set(section, key, JsonValue.Create(value));

    public void Set(string section, string key, IEnumerable<string> values)
        => SettingsFile.Set(section, key, new JsonArray([.. values.Select(value => JsonValue.Create(value))]));

    /// <summary>For settings read once at start-up (model choices): relaunches Jarvis to apply them.</summary>
    public static void RestartApp() => Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
}
