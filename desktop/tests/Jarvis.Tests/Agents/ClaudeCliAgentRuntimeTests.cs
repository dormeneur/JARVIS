using Jarvis.Contracts.Agents;
using Jarvis.Providers.ClaudeCli;

namespace Jarvis.Tests.Agents;

public sealed class ClaudeCliAgentRuntimeTests
{
    private static List<string> Allowed(AgentJob job)
    {
        var arguments = ClaudeCliAgentRuntime.Arguments(job, "sonnet", new Dictionary<string, string?>());
        var index = arguments.ToList().IndexOf("--allowedTools");
        return [.. arguments[index + 1].Split(' ')];
    }

    [Fact]
    public void Arguments_ScopeFileToolsToTheJobFolder()
    {
        var allowed = Allowed(new AgentJob("sys", "brief", "C:/jobs/one"));

        // A bare tool name matches every path on the PC (the vault, the Startup folder…).
        foreach (var bare in new[] { "Read", "Write", "Edit", "Glob", "Grep" })
        {
            Assert.DoesNotContain(bare, allowed);
        }

        Assert.Contains("Read(./**)", allowed);
        Assert.Contains("Edit(./**)", allowed);
        Assert.Contains("WebSearch", allowed);
    }

    [Fact]
    public void Arguments_DenyAnythingNotAllowed_AndAddConnectors()
    {
        var job = new AgentJob("sys", "brief", "C:/jobs/one", [new AgentConnector("linear", "https://mcp.linear.app/mcp", null)]);
        var arguments = ClaudeCliAgentRuntime.Arguments(job, "sonnet", new Dictionary<string, string?>());

        Assert.Equal("dontAsk", arguments[arguments.ToList().IndexOf("--permission-mode") + 1]);
        Assert.Contains("mcp__linear", Allowed(job));
        Assert.Contains("--mcp-config", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("Bash", StringComparison.Ordinal));
    }
}
