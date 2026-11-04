using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Core.Agents;

/// <summary>GitHub's answer to "start a sign-in": the code to show the user and where they enter it.</summary>
public sealed record GitHubDeviceCode(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

/// <summary>
/// Connect GitHub the way the gh CLI does (OAuth device flow): Jarvis asks GitHub for a short code,
/// the user approves Jarvis on github.com with it, and Jarvis receives a token. No client secret
/// exists anywhere: the flow needs only Jarvis's public client ID (its GitHub OAuth app, with
/// device flow on). Each GitHub account approves separately, so several sit side by side.
/// </summary>
public sealed class GitHubSignIn(HttpClient http, TimeProvider time)
{
    /// <summary>
    /// Jarvis's GitHub OAuth app (public by design: device flow has no secret). Empty until one is
    /// registered; then Connect is one click, and pasting a token stays as the fallback.
    /// </summary>
    public const string ClientId = "";

    /// <summary>Repositories (with issues and pull requests), organisations read, and who the user is.</summary>
    public const string Scope = "repo read:org read:user";

    public static bool Available => ClientId.Length > 0;

    public async Task<GitHubDeviceCode> StartAsync(CancellationToken cancellationToken, string clientId = ClientId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = Scope }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubDeviceCode>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("GitHub sent an empty sign-in reply.");
    }

    /// <summary>Waits for the user to approve on github.com; returns the token, or throws with why it didn't happen.</summary>
    public async Task<string> WaitAsync(GitHubDeviceCode code, CancellationToken cancellationToken, string clientId = ClientId)
    {
        ArgumentNullException.ThrowIfNull(code);
        var interval = TimeSpan.FromSeconds(Math.Max(code.Interval, 5));
        var until = time.GetUtcNow().AddSeconds(code.ExpiresIn);
        while (time.GetUtcNow() < until)
        {
            await Task.Delay(interval, time, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = code.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var reply = await response.Content.ReadFromJsonAsync<TokenReply>(cancellationToken).ConfigureAwait(false);
            switch (reply)
            {
                case { AccessToken: { Length: > 0 } token }:
                    return token;
                case { Error: "authorization_pending" }:
                    continue;
                case { Error: "slow_down" }:
                    interval += TimeSpan.FromSeconds(5); // GitHub asks for a longer wait
                    continue;
                case { Error: "access_denied" }:
                    throw new InvalidOperationException("The sign-in was cancelled on GitHub.");
                case { Error: "expired_token" }:
                    throw new InvalidOperationException("The code ran out before it was approved. Connect again for a fresh one.");
                default:
                    throw new InvalidOperationException($"GitHub didn't sign Jarvis in ({reply?.Error ?? "no reply"}).");
            }
        }

        throw new InvalidOperationException("The code ran out before it was approved. Connect again for a fresh one.");
    }

    private sealed record TokenReply(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("error")] string? Error);
}
