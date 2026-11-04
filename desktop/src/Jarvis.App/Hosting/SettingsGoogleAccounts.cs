using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Core.Agents;
using Jarvis.Core.Google;
using Jarvis.Platform.Shell;

namespace Jarvis.App.Hosting;

/// <summary>
/// Google sign-ins in settings.json under Google (the sign-in app's client ID and the accounts'
/// emails). The app's secret is in Windows Credential Manager ("Jarvis/google/app"), and each
/// account's tokens in a DPAPI-encrypted file, as for connectors.
/// </summary>
internal sealed class SettingsGoogleAccounts : IGoogleAccounts
{
    private const string Section = "Google";
    private const string SecretTarget = "Jarvis/google/app";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly McpOAuth _oauth;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private string? _clientId;
    private List<string> _accounts;

    public SettingsGoogleAccounts(McpOAuth oauth)
    {
        _oauth = oauth;
        var root = File.Exists(AppPaths.SettingsFile) ? JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) : null;
        _clientId = root?[Section]?["ClientId"]?.GetValue<string>();
        _accounts = root?[Section]?["Accounts"]?.Deserialize<List<string>>(Json) ?? [];
    }

    public string? ClientId
    {
        get
        {
            lock (_gate)
            {
                return _clientId;
            }
        }
    }

    public IReadOnlyList<string> Accounts
    {
        get
        {
            lock (_gate)
            {
                return [.. _accounts];
            }
        }
    }

    public void SetApp(string clientId, string clientSecret)
    {
        lock (_gate)
        {
            _clientId = clientId.Trim();
            CredentialVault.Save(SecretTarget, clientSecret.Trim());
            SettingsFile.Set(Section, "ClientId", JsonValue.Create(_clientId));
        }
    }

    public string? ClientSecret() => CredentialVault.Read(SecretTarget);

    public void Save(string email, OAuthTokens tokens)
    {
        lock (_gate)
        {
            ProtectedFile.Write(TokenFile(email), JsonSerializer.Serialize(tokens, Json));
            _accounts = [.. _accounts.Where(existing => !Same(existing, email)), email];
            SettingsFile.Set(Section, "Accounts", JsonSerializer.SerializeToNode(_accounts, Json));
        }
    }

    public void Remove(string email)
    {
        lock (_gate)
        {
            ProtectedFile.Delete(TokenFile(email));
            _accounts = [.. _accounts.Where(existing => !Same(existing, email))];
            SettingsFile.Set(Section, "Accounts", JsonSerializer.SerializeToNode(_accounts, Json));
        }
    }

    public async Task<string?> TokenAsync(string email, CancellationToken cancellationToken)
    {
        // One refresh at a time, as for connectors.
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = TokenFile(email);
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
                return null; // revoked, or the app was deleted: Settings offers to sign in again
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    private static string TokenFile(string email) => Path.Combine(AppPaths.DataDirectory, "connectors", $"google-{email.ToLowerInvariant()}.token");

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
