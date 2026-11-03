using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Jarvis.Core.Agents;

namespace Jarvis.Tests.Agents;

/// <summary>The whole sign-in against a fake MCP server + authorization server on loopback.</summary>
public sealed class McpOAuthTests
{
    [Fact]
    public async Task SignIn_DiscoversRegistersAndExchangesWithPkce_ThenRefreshes()
    {
        using var server = new FakeAuthServer();
        using var http = new HttpClient();
        var oauth = new McpOAuth(http, TimeProvider.System);

        // The "browser": follow the authorize page's redirect back to Jarvis's loopback callback.
        var tokens = await oauth.SignInAsync(
            server.McpUrl,
            page =>
            {
                _ = Task.Run(() => http.GetAsync(page, TestContext.Current.CancellationToken));
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("access-1", tokens.AccessToken);
        Assert.Equal("refresh-1", tokens.RefreshToken);
        Assert.Equal("client-42", tokens.ClientId);
        Assert.True(server.PkceMatched, "the token request must prove the PKCE verifier");
        Assert.Equal(server.McpUrl, server.ResourceSent);
        Assert.Equal("read write", server.ScopeSent);

        var refreshed = await oauth.RefreshAsync(tokens, TestContext.Current.CancellationToken);
        Assert.Equal("access-2", refreshed.AccessToken);
        Assert.Equal("refresh-1", refreshed.RefreshToken); // kept when the server doesn't rotate it
    }

    private sealed class FakeAuthServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _origin;
        private string? _challenge;

        public FakeAuthServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _origin = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(_origin + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string McpUrl => _origin + "/mcp";

        public bool PkceMatched { get; private set; }

        public string? ResourceSent { get; private set; }

        public string? ScopeSent { get; private set; }

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Handle(context);
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var body = request.HasEntityBody ? new StreamReader(request.InputStream).ReadToEnd() : string.Empty;
            switch (request.Url!.AbsolutePath)
            {
                case "/mcp":
                    response.StatusCode = 401;
                    response.AddHeader("WWW-Authenticate", $"Bearer resource_metadata=\"{_origin}/meta/resource\"");
                    break;
                case "/meta/resource":
                    Json(response, $$"""{"resource":"{{McpUrl}}","authorization_servers":["{{_origin}}/auth"],"scopes_supported":["read","write"]}""");
                    break;
                case "/.well-known/oauth-authorization-server/auth":
                    Json(response, $$"""{"issuer":"{{_origin}}/auth","authorization_endpoint":"{{_origin}}/auth/authorize","token_endpoint":"{{_origin}}/auth/token","registration_endpoint":"{{_origin}}/auth/register"}""");
                    break;
                case "/auth/register":
                    Json(response, """{"client_id":"client-42"}""");
                    break;
                case "/auth/authorize":
                    var query = request.QueryString;
                    _challenge = query["code_challenge"];
                    ResourceSent = query["resource"];
                    ScopeSent = query["scope"];
                    response.StatusCode = 302;
                    response.RedirectLocation = $"{query["redirect_uri"]}?code=code-7&state={Uri.EscapeDataString(query["state"]!)}";
                    break;
                case "/auth/token":
                    var form = HttpUtility.ParseQueryString(body);
                    if (form["grant_type"] == "authorization_code")
                    {
                        var verifier = form["code_verifier"] ?? string.Empty;
                        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                        PkceMatched = expected == _challenge && form["code"] == "code-7";
                        Json(response, """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600}""");
                    }
                    else
                    {
                        Json(response, """{"access_token":"access-2","expires_in":3600}""");
                    }

                    break;
                default:
                    response.StatusCode = 404;
                    break;
            }

            response.Close();
        }

        private static void Json(HttpListenerResponse response, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.OutputStream.Write(bytes);
        }
    }
}
