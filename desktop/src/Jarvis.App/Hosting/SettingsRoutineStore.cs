using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Agents;
using Microsoft.Extensions.Hosting;

namespace Jarvis.App.Hosting;

/// <summary>
/// Keeps routines in settings.json under Routines:Items. The list in memory is the source of truth
/// (the file reloads a moment after each write); every change is written straight through.
/// </summary>
internal sealed class SettingsRoutineStore : IRoutineStore
{
    private const string Section = "Routines";
    private const string Key = "Items";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private List<Routine> _routines;

    public SettingsRoutineStore()
    {
        // Read back with the same serializer that wrote it (the configuration binder can't build these records).
        var root = File.Exists(AppPaths.SettingsFile) ? JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) : null;
        _routines = root?[Section]?[Key]?.Deserialize<List<Routine>>(Json) ?? [];
    }

    public IReadOnlyList<Routine> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _routines];
            }
        }
    }

    public void Add(Routine routine) => Change(routines => [.. routines, routine]);

    public void Remove(string id) => Change(routines => [.. routines.Where(routine => routine.Id != id)]);

    public void MarkRun(string id, DateTimeOffset at)
        => Change(routines => [.. routines.Select(routine => routine.Id == id ? routine with { LastRun = at } : routine)]);

    private void Change(Func<List<Routine>, List<Routine>> change)
    {
        lock (_gate)
        {
            _routines = change(_routines);
            SettingsFile.Set(Section, Key, JsonSerializer.SerializeToNode(_routines, Json));
        }
    }
}

/// <summary>Lets the proactive layer look around every 30 seconds; a failed tick is logged, not fatal.</summary>
internal sealed partial class ProactiveTimer(Jarvis.Core.Proactive.ProactiveCoach coach, TimeProvider time, Microsoft.Extensions.Logging.ILogger<ProactiveTimer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await coach.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogTickFailed(exception);
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Proactive check failed")]
    private partial void LogTickFailed(Exception exception);
}

/// <summary>Checks for due routines every 30 seconds.</summary>
internal sealed class RoutineTimer(RoutineScheduler scheduler, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        do
        {
            scheduler.Tick();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
