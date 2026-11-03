namespace Jarvis.Contracts.Agents;

/// <summary>
/// Runs one piece of background work to completion in its own folder: research, writing, sorting
/// files. Implementations: Claude Code CLI (free with a Pro login).
/// </summary>
public interface IAgentRuntime
{
    /// <summary>Streams what the agent is doing; ends with <see cref="AgentEvent.Finished"/> or throws.</summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentJob job, CancellationToken cancellationToken);
}

/// <param name="Instructions">How to work (system prompt).</param>
/// <param name="Brief">What to do, written for the agent by the voice brain.</param>
/// <param name="WorkingDirectory">The job's folder; the agent may only touch files inside it.</param>
/// <param name="Connectors">Remote MCP servers the job may use.</param>
public sealed record AgentJob(string Instructions, string Brief, string WorkingDirectory, IReadOnlyList<AgentConnector>? Connectors = null);

/// <summary>A remote MCP server for a job; <paramref name="ApiKey"/> is sent as a bearer token.</summary>
public sealed record AgentConnector(string Name, string Url, string? ApiKey);

/// <summary>Closed set of things an agent reports while it works.</summary>
public abstract record AgentEvent
{
    private AgentEvent()
    {
    }

    /// <summary>Something the agent said along the way, e.g. "Found the three receipts."</summary>
    public sealed record Note(string Text) : AgentEvent;

    /// <summary>The agent used a tool, e.g. ("WebSearch", "best laptops 2026").</summary>
    public sealed record ToolUse(string Tool, string Detail) : AgentEvent;

    /// <summary>Done; <paramref name="Result"/> is its final message to the user.</summary>
    public sealed record Finished(string Result) : AgentEvent;
}
