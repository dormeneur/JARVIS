using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.App.Hosting;

/// <summary>
/// Writes individual values into settings.json. The configuration system watches the file, so
/// <c>IOptionsMonitor</c> consumers see the change within a moment, without a restart.
/// </summary>
internal static class SettingsFile
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly Lock Gate = new();

    public static void Set(string section, string key, JsonNode? value)
    {
        lock (Gate)
        {
            var root = File.Exists(AppPaths.SettingsFile)
                ? JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) as JsonObject ?? []
                : [];
            // "Brain:ClaudeCli" is a path: nested objects, the shape the configuration system reads.
            var node = root;
            foreach (var part in section.Split(':'))
            {
                if (node[part] is not JsonObject child)
                {
                    child = [];
                    node[part] = child;
                }

                node = child;
            }

            // Same value already there: no write, so no reload (which would close an open menu).
            if (JsonNode.DeepEquals(node[key], value))
            {
                return;
            }

            node[key] = value;

            // Write-then-rename so a crash mid-write never leaves a truncated settings file.
            var temporary = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(Indented));
            File.Move(temporary, AppPaths.SettingsFile, overwrite: true);
        }
    }
}
