using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Jarvis.Contracts.Agents;
using Microsoft.Extensions.Options;

namespace Jarvis.Providers.ClaudeCli;

/// <summary>
/// Background jobs on the user's Claude Code login: one `claude -p` per job, working in the job's
/// folder. Deliberately limited tools: read and write files there, search and fetch the web, and
/// the user's connectors (remote MCP servers they added in Settings). No shell, so a job can't run
/// programs or reach the rest of the PC. Isolated from the user's own Claude Code setup (no
/// settings, hooks, their MCP servers or CLAUDE.md), like the brain.
/// </summary>
public sealed class ClaudeCliAgentRuntime : IAgentRuntime
{
    private const string Tools = "Read,Write,Edit,Glob,Grep,WebSearch,WebFetch";
    private const int ErrorTailLength = 600;

    private readonly ClaudeCliOptions _options;

    public ClaudeCliAgentRuntime(IOptions<ClaudeCliOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentJob job, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var process = Start(job);
        var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                lines.Writer.TryComplete();
            }
            else
            {
                lines.Writer.TryWrite(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            lock (errors)
            {
                errors.AppendLine(e.Data);
                if (errors.Length > ErrorTailLength * 2)
                {
                    errors.Remove(0, errors.Length - ErrorTailLength);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The brief goes in on stdin (no length limits, no quoting); closing it starts the run.
        await process.StandardInput.WriteAsync(job.Brief.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        using var kill = cancellationToken.Register(() => Kill(process));
        await foreach (var line in lines.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var parsed in ClaudeProtocol.ParseAgent(line))
            {
                switch (parsed)
                {
                    case ClaudeOutput.Agent agent:
                        yield return agent.Event;
                        break;
                    case ClaudeOutput.Result { IsError: true } failure:
                        throw new InvalidOperationException($"Claude Code reported an error: {failure.Message}");
                    case ClaudeOutput.Result success:
                        yield return new AgentEvent.Finished(success.Message);
                        yield break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        string tail;
        lock (errors)
        {
            tail = errors.ToString().Trim();
        }

        throw new InvalidOperationException($"Claude Code exited before finishing the job. {tail}".Trim());
    }

    /// <summary>
    /// The --mcp-config JSON. Keys never appear in it (it shows in the process list): each header
    /// names an environment variable that Claude Code expands, set only on the job's process.
    /// </summary>
    internal static string McpConfig(IReadOnlyList<AgentConnector> connectors, IDictionary<string, string?> environment)
    {
        var servers = new System.Text.Json.Nodes.JsonObject();
        for (var i = 0; i < connectors.Count; i++)
        {
            var connector = connectors[i];
            var server = new System.Text.Json.Nodes.JsonObject { ["type"] = "http", ["url"] = connector.Url };
            if (connector.ApiKey is { Length: > 0 } key)
            {
                var variable = $"JARVIS_MCP_KEY_{i}";
                environment[variable] = key;
                server["headers"] = new System.Text.Json.Nodes.JsonObject { ["Authorization"] = $"Bearer ${{{variable}}}" };
            }

            servers[connector.Name] = server;
        }

        return new System.Text.Json.Nodes.JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    private Process Start(AgentJob job)
    {
        var start = new ProcessStartInfo(_options.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = job.WorkingDirectory,
        };
        var connectors = job.Connectors ?? [];
        var allowed = string.Join(' ', [.. Tools.Split(','), .. connectors.Select(connector => $"mcp__{connector.Name}")]);
        foreach (var argument in new[]
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--model", _options.Model,
            "--system-prompt", job.Instructions,
            "--tools", Tools,
            "--allowedTools", allowed,
            "--permission-mode", "acceptEdits",
            "--setting-sources", string.Empty,
            "--strict-mcp-config",
            "--no-session-persistence",
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (connectors.Count > 0)
        {
            start.ArgumentList.Add("--mcp-config");
            start.ArgumentList.Add(McpConfig(connectors, start.Environment));
        }

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start '{_options.Executable}'. Is Claude Code installed and on PATH?");
        ChildProcessJob.Track(process);
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }
}
