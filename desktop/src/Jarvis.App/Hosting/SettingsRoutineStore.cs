using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Agents;
using Jarvis.Core.Proactive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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

/// <summary>Hunches (Core/Proactive/HunchCoach): the same wiring on Windows and the Mac, which links this file.</summary>
internal static class HunchHosting
{
    public static void Add(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HunchOptions>(configuration.GetSection(HunchOptions.SectionName));
        services.AddSingleton(provider => new HunchCoach(
            provider.GetRequiredService<Jarvis.Contracts.Vault.IVault>(),
            provider.GetRequiredService<Jarvis.Contracts.Activity.IActivityWatch>(),
            provider.GetRequiredService<Jarvis.Contracts.Notch.INotchPresenter>(),
            provider.GetRequiredService<Jarvis.Core.PauseSwitch>(),
            provider.GetRequiredService<IOptionsMonitor<HunchOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HunchCoach>>()));

        // Jobs sent from the phone (jv-api /relay/jobs), run here as background jobs.
        services.AddSingleton(provider => new RelayCoach(
            provider.GetRequiredService<Jarvis.Contracts.Vault.IVault>(),
            provider.GetRequiredService<IAgentJobs>(),
            () => provider.GetRequiredService<Jarvis.Core.Companion.ScreenPrivacy>().PrivateMode,
            provider.GetRequiredService<Jarvis.Core.PauseSwitch>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RelayCoach>>()));
    }
}

/// <summary>Lets the proactive layer look around every 30 seconds; a failed tick is logged, not fatal.</summary>
internal sealed partial class ProactiveTimer(ProactiveCoach coach, HunchCoach hunches, RelayCoach relay, TimeProvider time, Microsoft.Extensions.Logging.ILogger<ProactiveTimer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            // Each on its own: a failing one never stops the other. The coach goes first, so a
            // meeting heads-up or the morning brief wins the notch over a hunch.
            foreach (var tick in (Func<CancellationToken, Task>[])[coach.TickAsync, hunches.TickAsync, relay.TickAsync])
            {
                try
                {
                    await tick(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    LogTickFailed(exception);
                }
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Proactive check failed")]
    private partial void LogTickFailed(Exception exception);
}

/// <summary>Checks for due routines every 30 seconds, except in private mode (routines run on Claude).</summary>
internal sealed class RoutineTimer(RoutineScheduler scheduler, Jarvis.Core.Companion.ScreenPrivacy privacy, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        do
        {
            if (!privacy.PrivateMode)
            {
                scheduler.Tick(); // a routine due meanwhile runs once private mode is off
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
