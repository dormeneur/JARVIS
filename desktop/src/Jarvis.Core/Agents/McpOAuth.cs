using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jarvis.Core.Agents;

/// <summary>What a browser sign-in leaves behind; enough to refresh without the user.</summary>
public sealed record OAuthTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string TokenEndpoint,
    string ClientId,
    string? ClientSecret,
    string Resource);

/// <summary>
/// Signs in to a remote MCP server the way its spec describes: discover the authorization server
/// (protected-resource metadata, falling back to the server's own origin), register Jarvis as a
/// public client (dynamic client registration), then the authorization-code flow with PKCE through
/// a loopback redirect. Refreshes tokens without the user.
/// </summary>
public sealed class McpOAuth
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public McpOAuth(HttpClient http, TimeProvider time)
    {
        _http = http;
        _time = time;
    }

    /// <param name="openBrowser">Shows the sign-in page to the user.</param>
    public async Task<OAuthTokens> SignInAsync(string serverUrl, Func<Uri, ValueTask> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        var (authorization, scope) = await DiscoverAsync(serverUrl, cancellationToken).ConfigureAwait(false);

        using var callback = LoopbackCallback.Start();
        var (clientId, clientSecret) = await RegisterAsync(authorization, callback.RedirectUri, cancellationToken).ConfigureAwait(false);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32)); // ponytail: crypto randomness, not injectable by design
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = callback.RedirectUri,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["resource"] = serverUrl,
            ["scope"] = scope,
        };
        await openBrowser(new Uri(authorization.AuthorizationEndpoint + (authorization.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?") + Form(query))).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        var reply = await callback.WaitAsync(timeout.Token).ConfigureAwait(false);
        if (reply["error"] is { } error)
        {
            throw new InvalidOperationException($"Sign-in was turned down ({error}).");
        }

        if (reply["state"] != state || reply["code"] is not { } code)
        {
            throw new InvalidOperationException("The sign-in reply didn't match this sign-in. Try again.");
        }

        return await RequestTokensAsync(
            authorization.TokenEndpoint,
            new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = callback.RedirectUri,
                ["code_verifier"] = verifier,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["resource"] = serverUrl,
            },
            previous: new OAuthTokens(string.Empty, null, null, authorization.TokenEndpoint, clientId, clientSecret, serverUrl),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the access token has run out, or will within a minute.</summary>
    public bool IsExpiring(OAuthTokens tokens)
        => tokens?.ExpiresAt is { } expires && expires - _time.GetUtcNow() < TimeSpan.FromMinutes(1);

    public Task<OAuthTokens> RefreshAsync(OAuthTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.RefreshToken is null)
        {
            throw new InvalidOperationException("The sign-in has expired. Sign in again in Settings → Connectors.");
        }

        return RequestTokensAsync(
            tokens.TokenEndpoint,
            new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.RefreshToken,
                ["client_id"] = tokens.ClientId,
                ["client_secret"] = tokens.ClientSecret,
                ["resource"] = tokens.Resource,
            },
            tokens,
            cancellationToken);
    }

    /// <summary>Where the server's authorization server is, and which scopes to ask for.</summary>
    internal async Task<(AuthorizationServer Server, string? Scope)> DiscoverAsync(string serverUrl, CancellationToken cancellationToken)
    {
        var server = new Uri(serverUrl);
        var origin = server.GetLeftPart(UriPartial.Authority);

        // The server's 401 names its protected-resource metadata; otherwise try the well-known places.
        var resourceMetadata = await FirstJsonAsync(
            [await ResourceMetadataHintAsync(server, cancellationToken).ConfigureAwait(false), $"{origin}/.well-known/oauth-protected-resource{server.AbsolutePath.TrimEnd('/')}", $"{origin}/.well-known/oauth-protected-resource"],
            cancellationToken).ConfigureAwait(false);
        var issuer = resourceMetadata?["authorization_servers"]?[0]?.GetValue<string>()?.TrimEnd('/') ?? origin;
        var scopes = resourceMetadata?["scopes_supported"]?.AsArray().Select(scope => scope?.GetValue<string>()).OfType<string>().ToList();

        var issuerUri = new Uri(issuer);
        var issuerOrigin = issuerUri.GetLeftPart(UriPartial.Authority);
        var issuerPath = issuerUri.AbsolutePath.TrimEnd('/');
        var metadata = await FirstJsonAsync(
            [$"{issuerOrigin}/.well-known/oauth-authorization-server{issuerPath}", $"{issuerOrigin}/.well-known/openid-configuration{issuerPath}", $"{issuer}/.well-known/openid-configuration"],
            cancellationToken).ConfigureAwait(false);

        var authorization = new AuthorizationServer(
            metadata?["authorization_endpoint"]?.GetValue<string>() ?? $"{issuerOrigin}/authorize",
            metadata?["token_endpoint"]?.GetValue<string>() ?? $"{issuerOrigin}/token",
            metadata?["registration_endpoint"]?.GetValue<string>() ?? (metadata is null ? $"{issuerOrigin}/register" : null));
        return (authorization, scopes is { Count: > 0 } ? string.Join(' ', scopes) : null);
    }

    private async Task<string?> ResourceMetadataHintAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(header => header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))?.Parameter;
            return challenge is null ? null : WwwAuthenticateParameter(challenge, "resource_metadata");
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>A quoted or bare parameter from a WWW-Authenticate challenge.</summary>
    internal static string? WwwAuthenticateParameter(string challenge, string name)
    {
        foreach (var part in challenge.Split(','))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim('"');
            }
        }

        return null;
    }

    private async Task<JsonNode?> FirstJsonAsync(IEnumerable<string?> urls, CancellationToken cancellationToken)
    {
        foreach (var url in urls.OfType<string>().Distinct())
        {
            try
            {
                using var response = await _http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException)
            {
                // Not there; try the next place.
            }
        }

        return null;
    }

    private async Task<(string ClientId, string? ClientSecret)> RegisterAsync(AuthorizationServer server, string redirectUri, CancellationToken cancellationToken)
    {
        if (server.RegistrationEndpoint is null)
        {
            throw new InvalidOperationException("This server doesn't let new apps register themselves, so Jarvis can't sign in to it. Use an API key if it offers one.");
        }

        var body = new JsonObject
        {
            ["client_name"] = "Jarvis",
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };
        using var response = await _http.PostAsync(new Uri(server.RegistrationEndpoint), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The server wouldn't register Jarvis ({(int)response.StatusCode}): {Shorten(text)}");
        }

        var registration = JsonNode.Parse(text);
        return (registration?["client_id"]?.GetValue<string>() ?? throw new InvalidOperationException("The server registered Jarvis without a client id."),
            registration?["client_secret"]?.GetValue<string>());
    }

    private async Task<OAuthTokens> RequestTokensAsync(string tokenEndpoint, Dictionary<string, string?> form, OAuthTokens previous, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form.Where(pair => pair.Value is not null).Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!)));
        using var response = await _http.PostAsync(new Uri(tokenEndpoint), content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The server didn't hand out a token ({(int)response.StatusCode}): {Shorten(text)}");
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(text) ?? throw new InvalidOperationException("The server sent an empty token reply.");
        return previous with
        {
            AccessToken = token.AccessToken ?? throw new InvalidOperationException("The token reply had no access token."),
            RefreshToken = token.RefreshToken ?? previous.RefreshToken, // servers may keep the old one
            ExpiresAt = token.ExpiresIn is { } seconds ? _time.GetUtcNow().AddSeconds(seconds) : null,
        };
    }

    private static string Shorten(string text) => text.Length > 200 ? text[..200] + "…" : text;

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Form(Dictionary<string, string?> values)
        => string.Join('&', values.Where(pair => pair.Value is not null).Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));

    internal sealed record AuthorizationServer(string AuthorizationEndpoint, string TokenEndpoint, string? RegistrationEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long? ExpiresIn);

    /// <summary>A one-shot HTTP listener on a free loopback port that catches the browser's redirect.</summary>
    private sealed class LoopbackCallback : IDisposable
    {
        private readonly HttpListener _listener;

        private LoopbackCallback(HttpListener listener, string redirectUri)
        {
            _listener = listener;
            RedirectUri = redirectUri;
        }

        public string RedirectUri { get; }

        public static LoopbackCallback Start()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/callback/");
            listener.Start();
            return new LoopbackCallback(listener, $"http://127.0.0.1:{port}/callback");
        }

        public async Task<System.Collections.Specialized.NameValueCollection> WaitAsync(CancellationToken cancellationToken)
        {
            using var stop = cancellationToken.Register(_listener.Stop);
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                throw new OperationCanceledException("Sign-in timed out or was cancelled.", exception, cancellationToken);
            }

            var reply = context.Request.QueryString; // read before Close disposes the request
            var page = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Jarvis</title><body style=\"font:16px system-ui;padding:48px\">You're signed in. You can close this tab and go back to Jarvis.</body>");
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.OutputStream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
            context.Response.Close();
            return reply;
        }

        public void Dispose() => ((IDisposable)_listener).Dispose();
    }
}
