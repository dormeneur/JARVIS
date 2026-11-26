using Jarvis.Contracts.Vault;
using Jarvis.Core.Agents;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Proactive;

/// <summary>
/// Jobs sent from the phone (jv-api's /relay/jobs): every tick, while Jarvis is running, connected
/// and not paused, the oldest queued one is claimed and started as an ordinary background job (so it
/// shows on the home's cards and the tray like any other), and its result is reported back for the
/// phone to show. Jobs run on Claude, so in private mode they wait, like routines do.
/// </summary>
public sealed partial class RelayCoach : IDisposable
{
    private readonly IVault _vault;
    private readonly IAgentJobs _jobs;
    private readonly Func<bool> _privateMode;
    private readonly PauseSwitch _pause;
    private readonly ILogger<RelayCoach> _logger;
    private readonly Lock _gate = new();

    /// <summary>Our background job → the phone's job it runs.</summary>
    private readonly Dictionary<Guid, string> _running = [];

    public RelayCoach(IVault vault, IAgentJobs jobs, Func<bool> privateMode, PauseSwitch pause, ILogger<RelayCoach> logger)
    {
        _vault = vault;
        _jobs = jobs;
        _privateMode = privateMode;
        _pause = pause;
        _logger = logger;
        _jobs.Changed += OnJobChanged;
    }

    public void Dispose() => _jobs.Changed -= OnJobChanged;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_pause.IsPaused || _privateMode() || !_vault.IsConnected)
        {
            return;
        }

        RelayJob? job;
        try
        {
            job = await _vault.ClaimRelayJobAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogVaultFailed("claim a job from your phone", exception.Message);
            return;
        }

        if (job is null)
        {
            return;
        }

        var started = _jobs.Start(job.Brief);
        lock (_gate)
        {
            _running[started.Id] = job.Id;
        }

        LogStarted(job.Id);
    }

    private void OnJobChanged(AgentJobInfo job)
    {
        string? relayId;
        lock (_gate)
        {
            if (job.Status is not (AgentJobStatus.Finished or AgentJobStatus.Failed or AgentJobStatus.Cancelled) || !_running.Remove(job.Id, out relayId))
            {
                return;
            }
        }

        var (status, result) = job.Status switch
        {
            AgentJobStatus.Finished => ("finished", job.Result ?? "Done."),
            AgentJobStatus.Cancelled => ("failed", "Stopped on the PC before it finished."),
            _ => ("failed", job.Result ?? "It didn't finish."),
        };
        _ = ReportAsync(relayId, status, result);
    }

    private async Task ReportAsync(string relayId, string status, string result)
    {
        try
        {
            await _vault.ReportRelayJobAsync(relayId, status, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            LogVaultFailed("report a job back to your phone", exception.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started job {Id} sent from the phone")]
    private partial void LogStarted(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relay: couldn't {What}: {Reason}")]
    private partial void LogVaultFailed(string what, string reason);
}
