using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Jarvis.Core.Agents;

/// <summary>Settings → Connectors' Test: a real MCP initialize request, answered in one sentence for the user.</summary>
public static class ConnectorCheck
{
    public static async Task<string> TestAsync(HttpClient http, string url, string? token)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"Jarvis","version":"1.0"}}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK or HttpStatusCode.Accepted => "Connected: jobs can use it.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => token is null
                    ? "The server wants you to sign in. Remove it and add it again with Sign in with browser."
                    : "The server turned the credentials down. Sign in again, or re-add it with a fresh key.",
                var status => $"The server answered {(int)status} {status}. Check the URL.",
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return $"Couldn't reach the server: {exception.Message}";
        }
    }
}
