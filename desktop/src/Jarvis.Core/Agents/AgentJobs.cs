using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Jarvis.Contracts.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Agents;

/// <summary>Starts, tracks and stops background jobs.</summary>
public interface IAgentJobs
{
    /// <summary>Every job this session, newest first.</summary>
    IReadOnlyList<AgentJobInfo> Jobs { get; }

    /// <summary>Raised whenever a job starts, reports progress, or ends. May be raised on any thread.</summary>
    event Action<AgentJobInfo>? Changed;

    /// <summary>Queues a job; it starts as soon as a slot is free.</summary>
    AgentJobInfo Start(string brief);

    void Cancel(Guid id);
}

/// <param name="Brief">What the job was asked to do, so it can be run again.</param>
public sealed record AgentJobInfo(
    Guid Id,
    string Title,
    string Folder,
    AgentJobStatus Status,
    DateTimeOffset Started,
    string? Activity = null,
    string? Result = null,
    string? Brief = null);

public enum AgentJobStatus
{
    Queued,
    Running,
    Finished,
    Failed,
    Cancelled,
}

public sealed class AgentOptions
{
    public const string SectionName = "Agents";

    /// <summary>Where job folders are created; the app sets this to its data folder.</summary>
    public string JobsDirectory { get; set; } = "jobs";

    /// <summary>Jobs beyond this wait for a free slot.</summary>
    public int MaximumConcurrent { get; set; } = 3;

    /// <summary>Model alias for agents (they do longer, harder work than a voice turn).</summary>
    public string Model { get; set; } = "sonnet";

    /// <summary>Read the first sentence of a finished job aloud; off shows it in the notch only.</summary>
    public bool SpeakResults { get; set; } = true;
}

/// <summary>
/// Runs each job in its own folder (<c>output/</c> for results, <c>tmp/</c> for scratch), a few at a
/// time, and keeps a snapshot of each one's state for the UI.
/// </summary>
public sealed partial class AgentJobManager : IAgentJobs, IDisposable
{
    private const int TitleWords = 6;

    private readonly IAgentRuntime _runtime;
    private readonly AgentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentJobManager> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, AgentJobInfo> _jobs = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    private readonly IConnectorStore? _connectors;

    public AgentJobManager(IAgentRuntime runtime, IOptions<AgentOptions> options, TimeProvider time, ILogger<AgentJobManager> logger, IConnectorStore? connectors = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _connectors = connectors;
        _runtime = runtime;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, _options.MaximumConcurrent));
    }

    public event Action<AgentJobInfo>? Changed;

    public IReadOnlyList<AgentJobInfo> Jobs => [.. _jobs.Values.OrderByDescending(job => job.Started)];

    public AgentJobInfo Start(string brief)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brief);
        brief = Jarvis.Core.Assistant.OutboundScanner.Redact(brief); // it goes to Claude
        var started = _time.GetLocalNow();
        var title = Title(brief);
        var folder = Path.Combine(_options.JobsDirectory, $"{started:yyyyMMdd-HHmmss}-{Slug(title)}");
        Directory.CreateDirectory(Path.Combine(folder, "output"));
        Directory.CreateDirectory(Path.Combine(folder, "tmp"));
        File.WriteAllText(Path.Combine(folder, "brief.md"), brief);

        var job = new AgentJobInfo(Guid.NewGuid(), title, folder, AgentJobStatus.Queued, started, Brief: brief);
        var cancellation = new CancellationTokenSource();
        _running[job.Id] = cancellation;
        Update(job);
        _ = RunAsync(job, brief, cancellation.Token);
        return job;
    }

    public void Cancel(Guid id)
    {
        if (_running.TryGetValue(id, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    public void Dispose()
    {
        foreach (var cancellation in _running.Values)
        {
            cancellation.Cancel();
        }

        _slots.Dispose();
    }

    private async Task RunAsync(AgentJobInfo job, string brief, CancellationToken cancellationToken)
    {
        try
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                job = Update(job with { Status = AgentJobStatus.Running, Activity = "Getting started" });
                LogStarted(job.Id, job.Title);
                string? result = null;
                await foreach (var agentEvent in _runtime.RunAsync(new AgentJob(AgentInstructions.Text, brief, Path.GetFullPath(job.Folder), await ConnectorsAsync(cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false))
                {
                    switch (agentEvent)
                    {
                        case AgentEvent.Note note:
                            job = Update(job with { Activity = FirstLine(note.Text) });
                            break;
                        case AgentEvent.ToolUse tool:
                            job = Update(job with { Activity = Describe(tool) });
                            break;
                        case AgentEvent.Finished finished:
                            result = finished.Result.Trim();
                            break;
                    }
                }

                if (result is null)
                {
                    throw new InvalidOperationException("The agent stopped without a final message.");
                }

                // Its files are in place before anyone hears it finished (Open shows them).
                CollectStrayOutput(job.Folder);
                File.WriteAllText(Path.Combine(job.Folder, "result.md"), result);
                job = Update(job with { Status = AgentJobStatus.Finished, Activity = null, Result = result });
                LogFinished(job.Id, (_time.GetLocalNow() - job.Started).TotalSeconds);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Update(job with { Status = AgentJobStatus.Cancelled, Activity = null });
        }
        catch (Exception exception)
        {
            LogFailed(job.Id, exception);
            Update(job with { Status = AgentJobStatus.Failed, Activity = null, Result = exception.Message });
        }
        finally
        {
            if (_running.TryRemove(job.Id, out var cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    /// <summary>Connectors as of now, so a job started after a change uses it.</summary>
    private async Task<List<AgentConnector>> ConnectorsAsync(CancellationToken cancellationToken)
    {
        var connectors = new List<AgentConnector>();
        foreach (var connector in _connectors?.All ?? [])
        {
            var token = await _connectors!.TokenAsync(connector.Name, cancellationToken).ConfigureAwait(false);
            connectors.Add(new AgentConnector(connector.Name, connector.Url, token));
        }

        return connectors;
    }

    /// <summary>
    /// Deliverables belong in output/, but an agent sometimes saves next to the brief instead.
    /// Move anything it left at the top of the job folder into output/ so "Open" finds it.
    /// </summary>
    internal static void CollectStrayOutput(string folder)
    {
        var output = Path.Combine(folder, "output");
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name is not ("brief.md" or "result.md"))
            {
                File.Move(file, Path.Combine(output, name), overwrite: true);
            }
        }
    }

    private AgentJobInfo Update(AgentJobInfo job)
    {
        _jobs[job.Id] = job;
        Changed?.Invoke(job);
        return job;
    }

    /// <summary>Tool calls in words a person would use.</summary>
    internal static string Describe(AgentEvent.ToolUse tool) => tool.Tool switch
    {
        "WebSearch" => $"Searching the web for {tool.Detail}",
        "WebFetch" => $"Reading {tool.Detail}",
        "Write" or "Edit" => $"Writing {tool.Detail}",
        "Read" => $"Reading {tool.Detail}",
        _ => "Working",
    };

    internal static string Title(string brief)
    {
        var words = brief.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(' ', words.Take(TitleWords)).TrimEnd('.', ',', ':', ';');
        return words.Length > TitleWords ? title + "…" : title;
    }

    private static string Slug(string title)
    {
        var slug = new StringBuilder();
        foreach (var c in title.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().Trim('-') is { Length: > 0 } result ? result[..Math.Min(40, result.Length)].TrimEnd('-') : "job";
    }

    private static string FirstLine(string text) => text.Trim().Split('\n')[0].Trim();

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent job {Id} started: {Title}")]
    private partial void LogStarted(Guid id, string title);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent job {Id} finished in {Seconds:F0} s")]
    private partial void LogFinished(Guid id, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent job {Id} failed")]
    private partial void LogFailed(Guid id, Exception exception);
}
