using System.Text.RegularExpressions;

namespace Jarvis.Core.Agents;

public enum ConnectorAuth
{
    /// <summary>The server needs no credentials.</summary>
    None,

    /// <summary>A key the user pasted, sent as a bearer token.</summary>
    ApiKey,

    /// <summary>Browser sign-in (OAuth); Jarvis keeps and refreshes the tokens.</summary>
    SignIn,
}

/// <summary>
/// A remote MCP server background jobs may use (Linear, Notion, an internal tool…). Its credentials,
/// if any, are kept apart in Windows-protected storage and never in settings.json.
/// </summary>
/// <param name="Name">Letters, digits, '-' and '_' (MCP tool names are built from it).</param>
/// <param name="Url">The server's HTTP endpoint.</param>
public sealed partial record Connector(string Name, string Url, ConnectorAuth Auth)
{
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    /// <summary>https anywhere, or plain http on this PC (local servers).</summary>
    public static bool IsValidUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex ValidName();
}

/// <summary>Where connectors (and their credentials) are kept. Thread-safe.</summary>
public interface IConnectorStore
{
    IReadOnlyList<Connector> All { get; }

    /// <summary>Adds or replaces by name; <paramref name="apiKey"/> is stored protected when given.</summary>
    void Save(Connector connector, string? apiKey);

    /// <summary>Adds or replaces a sign-in connector with the tokens its sign-in produced.</summary>
    void SaveSignIn(Connector connector, OAuthTokens tokens);

    void Remove(string name);

    /// <summary>
    /// The bearer token to send now: the API key, or a fresh access token (refreshed if it's about
    /// to run out). <see langword="null"/> when there's none, or the sign-in can't be refreshed.
    /// </summary>
    Task<string?> TokenAsync(string name, CancellationToken cancellationToken);
}
