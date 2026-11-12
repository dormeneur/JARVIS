using System.Net;
using Jarvis.Core.Agents;

namespace Jarvis.Tests.Agents;

public sealed class ConnectorCheckTests
{
    private sealed class Answer(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string Body { get; private set; } = string.Empty;

        public string? Token { get; private set; }

        // The request is disposed once TestAsync returns, so what's checked is copied here.
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Token = request.Headers.Authorization?.Parameter;
            return new HttpResponseMessage(status);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, null, "Connected")]
    [InlineData(HttpStatusCode.Unauthorized, null, "wants you to sign in")]
    [InlineData(HttpStatusCode.Forbidden, "key", "turned the credentials down")]
    [InlineData(HttpStatusCode.NotFound, null, "404")]
    public async Task Initialize_IsSentAndTheAnswerExplained(HttpStatusCode status, string? token, string said)
    {
        var handler = new Answer(status);
        using var http = new HttpClient(handler);

        var result = await ConnectorCheck.TestAsync(http, "https://mcp.example.test/mcp", token);

        Assert.Contains(said, result, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Contains("\"initialize\"", handler.Body, StringComparison.Ordinal);
        Assert.Equal(token, handler.Token);
    }
}
