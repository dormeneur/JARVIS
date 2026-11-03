using Jarvis.Contracts.Agents;
using Jarvis.Core.Agents;
using Jarvis.Providers.ClaudeCli;

namespace Jarvis.Tests.Agents;

public sealed class ConnectorTests
{
    [Fact]
    public void McpConfig_ReferencesKeysByEnvironmentVariable_NeverInline()
    {
        var environment = new Dictionary<string, string?>();

        var json = ClaudeCliAgentRuntime.McpConfig(
            [new AgentConnector("linear", "https://mcp.linear.app/mcp", "secret-key"), new AgentConnector("wiki", "https://mcp.example.com/mcp", null)],
            environment);

        Assert.DoesNotContain("secret-key", json, StringComparison.Ordinal);
        Assert.Contains("\"Authorization\":\"Bearer ${JARVIS_MCP_KEY_0}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"wiki\":{\"type\":\"http\",\"url\":\"https://mcp.example.com/mcp\"}", json, StringComparison.Ordinal);
        Assert.Equal("secret-key", environment["JARVIS_MCP_KEY_0"]);
        Assert.Single(environment);
    }

    [Theory]
    [InlineData("linear", true)]
    [InlineData("my_tool-2", true)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void Names_AreToolSafe(string name, bool valid) => Assert.Equal(valid, Connector.IsValidName(name));

    [Theory]
    [InlineData("https://mcp.linear.app/mcp", true)]
    [InlineData("http://localhost:3000/mcp", true)]
    [InlineData("http://example.com/mcp", false)] // keys must not travel unencrypted
    [InlineData("not a url", false)]
    public void Urls_AreHttpsOrLocal(string url, bool valid) => Assert.Equal(valid, Connector.IsValidUrl(url));

    [Theory]
    [InlineData("Bearer error=\"invalid_token\", resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"", "https://mcp.example.com/.well-known/oauth-protected-resource")]
    [InlineData("Bearer realm=\"x\"", null)]
    public void ResourceMetadata_IsReadFromTheChallenge(string challenge, string? expected)
        => Assert.Equal(expected, McpOAuth.WwwAuthenticateParameter(challenge.Replace("Bearer ", string.Empty, StringComparison.Ordinal), "resource_metadata"));
}
