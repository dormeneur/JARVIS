using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Agents;
using Jarvis.Platform.Shell;

namespace Jarvis.App.Hosting;

/// <summary>
/// Connectors in settings.json under Connectors:Items (name, URL, how it signs in). Credentials
/// never go there: API keys live in Windows Credential Manager ("Jarvis/connector/&lt;name&gt;"), and
/// sign-in tokens in a DPAPI-encrypted file per connector (they're too long for Credential Manager).
/// </summary>
internal sealed class SettingsConnectorStore : IConnectorStore
{
    private const string Section = "Connectors";
    private const string Key = "Items";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly McpOAuth _oauth;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private List<Connector> _connectors;

    public SettingsConnectorStore(McpOAuth oauth)
    {
        _oauth = oauth;
        var root = File.Exists(AppPaths.SettingsFile) ? JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) : null;
        _connectors = root?[Section]?[Key]?.Deserialize<List<Connector>>(Json) ?? [];
    }

    public IReadOnlyList<Connector> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _connectors];
            }
        }
    }

    public void Save(Connector connector, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(connector);
        lock (_gate)
        {
            ForgetCredentials(connector.Name);
            if (apiKey is { Length: > 0 })
            {
                CredentialVault.Save(VaultTarget(connector.Name), apiKey);
            }

            Replace(connector);
        }
    }

    public void SaveSignIn(Connector connector, OAuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(connector);
        lock (_gate)
        {
            ForgetCredentials(connector.Name);
            ProtectedFile.Write(TokenFile(connector.Name), JsonSerializer.Serialize(tokens, Json));
            Replace(connector);
        }
    }

    public void Remove(string name)
    {
        lock (_gate)
        {
            ForgetCredentials(name);
            _connectors = [.. _connectors.Where(existing => !Same(existing.Name, name))];
            Write();
        }
    }

    public async Task<string?> TokenAsync(string name, CancellationToken cancellationToken)
    {
        var connector = All.FirstOrDefault(existing => Same(existing.Name, name));
        switch (connector?.Auth)
        {
            case ConnectorAuth.ApiKey:
                return CredentialVault.Read(VaultTarget(name));
            case ConnectorAuth.SignIn:
                break;
            default:
                return null;
        }

        // One refresh at a time: refresh tokens are often single-use.
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = TokenFile(name);
            var tokens = ProtectedFile.Read(file) is { } text ? JsonSerializer.Deserialize<OAuthTokens>(text, Json) : null;
            if (tokens is null || !_oauth.IsExpiring(tokens))
            {
                return tokens?.AccessToken;
            }

            try
            {
                tokens = await _oauth.RefreshAsync(tokens, cancellationToken).ConfigureAwait(false);
                ProtectedFile.Write(file, JsonSerializer.Serialize(tokens, Json));
                return tokens.AccessToken;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            {
                return null; // the job runs without it; Settings shows "sign in again" on Test
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    private static string VaultTarget(string name) => $"Jarvis/connector/{name}";

    private static string TokenFile(string name) => Path.Combine(AppPaths.DataDirectory, "connectors", $"{name.ToLowerInvariant()}.token");

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void ForgetCredentials(string name)
    {
        CredentialVault.Delete(VaultTarget(name));
        ProtectedFile.Delete(TokenFile(name));
    }

    private void Replace(Connector connector)
    {
        _connectors = [.. _connectors.Where(existing => !Same(existing.Name, connector.Name)), connector];
        Write();
    }

    private void Write() => SettingsFile.Set(Section, Key, JsonSerializer.SerializeToNode(_connectors, Json));
}
