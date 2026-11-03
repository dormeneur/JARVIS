using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts.Browser;
using Jarvis.Contracts.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Providers.Local;

/// <summary>Vault settings, bound from the "Vault" settings section.</summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>The vault server (jv-api). It listens on this PC only.</summary>
    public string Address { get; set; } = "http://127.0.0.1:8000";
}

/// <summary>Where this PC's device secret is kept (Windows Credential Manager in the app).</summary>
public sealed record VaultCredentials(Func<string?> Read, Action<string?> Write);

/// <summary>
/// The vault through jv-api, as a registered device like the user's phone: the same JWT auth and
/// the same server-side /Secrets exclusion. Registers with a one-time invite code, keeps the
/// device secret in <see cref="VaultCredentials"/>, and gets a fresh token from it when needed.
/// </summary>
public sealed partial class JvApiVault : IVault
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<VaultOptions> _options;
    private readonly VaultCredentials _credentials;
    private readonly ILogger<JvApiVault> _logger;
    private readonly SemaphoreSlim _signIn = new(1, 1);
    private string? _token;

    public JvApiVault(HttpClient http, IOptionsMonitor<VaultOptions> options, VaultCredentials credentials, ILogger<JvApiVault> logger)
    {
        _http = http;
        _options = options;
        _credentials = credentials;
        _logger = logger;
    }

    public bool IsConnected => Device() is not null;

    private string Address => _options.CurrentValue.Address.TrimEnd('/');

    public async Task ConnectAsync(string inviteCode, CancellationToken cancellationToken)
    {
        var name = $"Jarvis on {Environment.MachineName}";
        var body = new JsonObject { ["device_name"] = name, ["invite_token"] = inviteCode.Trim(), ["server_url"] = Address };
        using var response = await SendRawAsync(HttpMethod.Post, "/auth/invite/register", body, token: null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new VaultException("That invite code didn't work. Codes last 10 minutes and work once; make a new one and try again.");
        }

        var registered = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        var secret = registered?["device_secret"]?.GetValue<string>() ?? throw new VaultException("The vault server sent an unexpected reply.");
        _credentials.Write(new JsonObject { ["name"] = name, ["secret"] = secret }.ToJsonString());
        _token = registered["access_token"]?.GetValue<string>();
        LogConnected();
    }

    public void Disconnect()
    {
        _credentials.Write(null);
        _token = null;
    }

    public async IAsyncEnumerable<FormAnswer> AnswerFormAsync(string title, IReadOnlyList<FormQuestion> questions, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var body = new JsonObject
        {
            ["title"] = title,
            ["attachments"] = Strings(attachments),
            ["questions"] = new JsonArray([.. questions.Select(question => (JsonNode)new JsonObject
            {
                ["id"] = question.Id,
                ["text"] = question.Text,
                ["kind"] = question.Kind,
                ["options"] = new JsonArray([.. question.Options.Select(option => (JsonNode)JsonValue.Create(option))]),
                ["required"] = question.Required,
            })]),
        };
        await foreach (var line in StreamAsync("/ask/ai/form-answers", body, cancellationToken).ConfigureAwait(false))
        {
            if (line["error"] is { } error)
            {
                LogBrainError(error.ToString()); // a failed batch still sends its questions as unknown
            }
            else if (line["id"]?.GetValue<string>() is { } id)
            {
                yield return new FormAnswer(id, line["answer"]?.GetValue<string>(), line["source"]?.GetValue<string>(), line["open"]?.GetValue<bool>() == true);
            }
        }
    }

    public async IAsyncEnumerable<string> ComposeAnswerAsync(string question, string title, string research, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["question"] = question, ["title"] = title, ["research"] = research, ["attachments"] = Strings(attachments) };
        await foreach (var line in StreamAsync("/ask/ai/compose-answer", body, cancellationToken).ConfigureAwait(false))
        {
            if (line["error"] is { } error)
            {
                throw new VaultException($"The vault's brain couldn't draft it: {error}");
            }

            if (line["token"]?.GetValue<string>() is { Length: > 0 } token)
            {
                yield return token;
            }
        }
    }

    public async Task<IReadOnlyList<VaultEntry>> ListAsync(string folder, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, FilePath(folder), null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        EnsureOk(response);
        var listing = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        return [.. (listing?["entries"] as JsonArray ?? []).OfType<JsonObject>().Select(entry => new VaultEntry(
            entry["path"]?.GetValue<string>() ?? string.Empty,
            entry["name"]?.GetValue<string>() ?? string.Empty,
            entry["type"]?.GetValue<string>() == "directory",
            DateTimeOffset.TryParse(entry["last_modified"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var modified) ? modified : null))];
    }

    public async Task UploadAsync(string path, string localFile, CancellationToken cancellationToken)
    {
        await EnsureFolderAsync(path[..Math.Max(0, path.LastIndexOf('/'))], cancellationToken).ConfigureAwait(false);
        var token = _token ?? await SignInAsync(null, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            await using var file = File.OpenRead(localFile);
            using var form = new MultipartFormDataContent { { new StreamContent(file), "file", Path.GetFileName(localFile) } };
            using var request = new HttpRequestMessage(HttpMethod.Post, Address + "/upload/" + Escaped(path)) { Content = form };
            request.Headers.Authorization = new("Bearer", token);
            using var response = await SendOrExplainAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                token = await SignInAsync(token, cancellationToken).ConfigureAwait(false);
                continue;
            }

            EnsureOk(response);
            return;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        // A folder's files go one by one, so the server's version records go with them.
        foreach (var entry in await ListAsync(path, cancellationToken).ConfigureAwait(false))
        {
            await DeleteAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        }

        using var response = await SendAsync(HttpMethod.Delete, FilePath(path), null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            EnsureOk(response);
        }
    }

    /// <summary>Creates a vault folder and its parents; existing ones are fine.</summary>
    private async Task EnsureFolderAsync(string folder, CancellationToken cancellationToken)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            using var response = await SendAsync(HttpMethod.Post, FilePath(string.Join('/', parts[..i])), new JsonObject { ["type"] = "directory" }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
            {
                EnsureOk(response);
            }
        }
    }

    private static JsonArray Strings(IReadOnlyList<string> values) => [.. values.Select(value => (JsonNode)JsonValue.Create(value))];

    public async Task ReindexAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/ask/reindex", null, cancellationToken).ConfigureAwait(false);
        EnsureOk(response);
    }

    public async IAsyncEnumerable<string> AskAsync(string question, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["query"] = question, ["options"] = new JsonObject { ["stream"] = true } };
        await foreach (var line in StreamAsync("/ask/ai/query", body, cancellationToken).ConfigureAwait(false))
        {
            if (line["error"] is { } error)
            {
                throw new VaultException($"The vault's brain failed: {error}");
            }

            if (line["token"]?.GetValue<string>() is { Length: > 0 } token)
            {
                yield return token;
            }
        }
    }

    public async Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, FilePath(path), null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureOk(response);
        var file = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        return file?["content"]?.GetValue<string>();
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        using var updated = await SendAsync(HttpMethod.Put, FilePath(path), new JsonObject { ["content"] = content }, cancellationToken).ConfigureAwait(false);
        if (updated.StatusCode != HttpStatusCode.NotFound)
        {
            EnsureOk(updated);
            return;
        }

        using var created = await SendAsync(HttpMethod.Post, FilePath(path), new JsonObject { ["content"] = content, ["type"] = "file" }, cancellationToken).ConfigureAwait(false);
        EnsureOk(created);
    }

    /// <summary>NDJSON lines (buffered to newlines, as the stream may split a line).</summary>
    private async IAsyncEnumerable<JsonObject> StreamAsync(string path, JsonObject body, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
        EnsureOk(response);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } text)
        {
            if (Parse(text) is { } line)
            {
                yield return line;
            }
        }
    }

    internal static JsonObject? Parse(string line)
    {
        try
        {
            return line.Length == 0 ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sends with this device's token, signing in again once if it was refused.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        var token = _token ?? await SignInAsync(null, cancellationToken).ConfigureAwait(false);
        var response = await SendRawAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        token = await SignInAsync(token, cancellationToken).ConfigureAwait(false);
        return await SendRawAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A fresh token from the device secret (unless another call already replaced <paramref name="stale"/>).</summary>
    private async Task<string> SignInAsync(string? stale, CancellationToken cancellationToken)
    {
        await _signIn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } current && current != stale)
            {
                return current;
            }

            var device = Device() ?? throw new VaultException("Jarvis isn't connected to your vault. Connect it in Settings → Vault.");
            var body = new JsonObject { ["device_name"] = device.Name, ["device_secret"] = device.Secret };
            using var response = await SendRawAsync(HttpMethod.Post, "/auth/reconnect", body, token: null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new VaultException("The vault server no longer accepts this PC (was it removed from your devices?). Connect it again in Settings → Vault.");
            }

            var reply = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
            return _token = reply?["access_token"]?.GetValue<string>() ?? throw new VaultException("The vault server sent an unexpected reply.");
        }
        finally
        {
            _signIn.Release();
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, JsonObject? body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Address + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return await SendOrExplainAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOrExplainAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new VaultException($"Can't reach your vault server at {Address}. Is the JARVIS server running (docker compose up -d)?", exception);
        }
    }

    private static void EnsureOk(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new VaultException($"The vault server said {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static string FilePath(string path) => "/files/" + Escaped(path);

    private static string Escaped(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private (string Name, string Secret)? Device()
    {
        if (_credentials.Read() is not { } saved || Parse(saved) is not { } device
            || device["name"]?.GetValue<string>() is not { } name || device["secret"]?.GetValue<string>() is not { } secret)
        {
            return null;
        }

        return (name, secret);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the vault as a new device")]
    private partial void LogConnected();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The vault's brain reported: {Error}")]
    private partial void LogBrainError(string error);
}
