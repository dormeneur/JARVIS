using Jarvis.Core.Agents;

namespace Jarvis.Tests.Agents;

public sealed class ConnectorCatalogTests
{
    private static readonly CatalogService GitHub = ConnectorCatalog.All.Single(service => service.Id == "github");

    [Fact]
    public void EachAccountOfAServiceGetsItsOwnName()
    {
        Assert.Equal("github", ConnectorCatalog.NameFor(GitHub, null, []));
        Assert.Equal("github-octo-cat", ConnectorCatalog.NameFor(GitHub, "Octo Cat", ["github"]));
        Assert.Equal("github-work-2", ConnectorCatalog.NameFor(GitHub, "work", ["github", "GITHUB-WORK"]));
        Assert.Equal("github-2", ConnectorCatalog.NameFor(GitHub, "  ", ["github"]));
        Assert.True(Connector.IsValidName(ConnectorCatalog.NameFor(GitHub, new string('x', 80), [])));
    }

    [Fact]
    public void KnowsWhichServiceAConnectorCameFrom_AndOnlyListsValidOnes()
    {
        Assert.Equal("GitHub", ConnectorCatalog.Of(new Connector("github-work", "https://api.githubcopilot.com/mcp/", ConnectorAuth.ApiKey))!.Name);
        Assert.Null(ConnectorCatalog.Of(new Connector("mine", "https://mcp.example.test/mcp", ConnectorAuth.None)));
        Assert.All(ConnectorCatalog.All, service =>
        {
            Assert.True(Connector.IsValidName(service.Id));
            Assert.True(Connector.IsValidUrl(service.Url));
            Assert.True(service.SignIn != CatalogSignIn.Token || service.TokenPage is not null);
        });
        Assert.Equal(ConnectorCatalog.All.Count, ConnectorCatalog.All.Select(service => service.Id).Distinct().Count());
    }
}
