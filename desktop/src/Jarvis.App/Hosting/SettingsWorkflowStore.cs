using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Assistant;

namespace Jarvis.App.Hosting;

/// <summary>Taught workflows in settings.json under Workflows:Items. Thread-safe.</summary>
internal sealed class SettingsWorkflowStore : IWorkflowStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();
    private List<Workflow> _taught;

    public SettingsWorkflowStore()
    {
        var root = File.Exists(AppPaths.SettingsFile) ? JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) : null;
        _taught = root?["Workflows"]?["Items"]?.Deserialize<List<Workflow>>(Json) ?? [];
    }

    public IReadOnlyList<Workflow> Taught
    {
        get
        {
            lock (_gate)
            {
                return [.. _taught];
            }
        }
    }

    public void Save(Workflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        lock (_gate)
        {
            _taught = [.. _taught.Where(kept => !kept.Phrases.Intersect(workflow.Phrases, StringComparer.OrdinalIgnoreCase).Any()), workflow];
            SettingsFile.Set("Workflows", "Items", JsonSerializer.SerializeToNode(_taught, Json));
        }
    }
}
