using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Core.Agents;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class ConnectorsViewModel : ObservableObject
{
    private const string NoAuth = "None";
    private const string ApiKeyAuth = "API key";
    private const string SignInAuth = "Sign in with browser";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly IConnectorStore _store;
    private readonly McpOAuth _oauth;

    public ConnectorsViewModel(IConnectorStore store, McpOAuth oauth)
    {
        _store = store;
        _oauth = oauth;
        Refresh();
    }

    public ObservableCollection<ConnectorRow> Connectors { get; } = [];

    /// <summary>The catalog, filtered by <see cref="Search"/>: one Connect (or Add another account) each.</summary>
    public ObservableCollection<CatalogRow> Available { get; } = [];

    private readonly List<CatalogRow> _catalog = [.. ConnectorCatalog.All.Select(service => new CatalogRow(service))];

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    partial void OnSearchChanged(string value) => Filter();

    public bool IsEmpty => Connectors.Count == 0;

    public IReadOnlyList<string> AuthChoices { get; } = [NoAuth, ApiKeyAuth, SignInAuth];

    [ObservableProperty]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsKey))]
    public partial string SelectedAuth { get; set; } = NoAuth;

    [ObservableProperty]
    public partial string NewKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormMessage { get; set; } = string.Empty;

    public bool NeedsKey => SelectedAuth == ApiKeyAuth;

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        var url = NewUrl.Trim();
        if (!Connector.IsValidName(name))
        {
            FormMessage = "Give it a short name of letters, digits, - or _ (for example linear).";
            return;
        }

        if (!Connector.IsValidUrl(url))
        {
            FormMessage = "The server URL must start with https:// (or http://localhost for a server on this PC).";
            return;
        }

        if (NeedsKey && NewKey.Trim().Length == 0)
        {
            FormMessage = "Paste the API key, or choose another kind of sign-in.";
            return;
        }

        if (SelectedAuth == SignInAuth)
        {
            FormMessage = "Sign in in the browser window that just opened…";
            if (await SignInAsync(new Connector(name, url, ConnectorAuth.SignIn)).ConfigureAwait(true) is { } failure)
            {
                FormMessage = failure;
                return;
            }

            FormMessage = $"Added {name}: signed in, jobs can use it.";
        }
        else
        {
            var key = NeedsKey ? NewKey.Trim() : null;
            FormMessage = "Checking the server…";
            var check = await TestAsync(url, key).ConfigureAwait(true);
            _store.Save(new Connector(name, url, key is null ? ConnectorAuth.None : ConnectorAuth.ApiKey), key);
            FormMessage = $"Added {name}. {check}";
        }

        NewName = NewUrl = NewKey = string.Empty;
        Refresh();
    }

    /// <summary>
    /// Connect from the catalog. Browser sign-in: Jarvis registers itself with the service and the
    /// user approves in the browser. Token: the first press shows where to make one; with a token
    /// pasted, it's checked against the service (GitHub names the account) and kept. Every new
    /// connection is then tested, so the row says whether jobs can really use it.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync(CatalogRow row)
    {
        var service = row.Service;
        var taken = _store.All.Select(connector => connector.Name).ToList();
        if (service.SignIn == CatalogSignIn.Browser)
        {
            var name = ConnectorCatalog.NameFor(service, row.Label, taken);
            row.Status = $"Sign in to {service.Name} in the browser window that just opened. Signed in to more than one account? Pick the one you want there.";
            row.Busy = true;
            var failure = await SignInAsync(new Connector(name, service.Url, ConnectorAuth.SignIn)).ConfigureAwait(true);
            row.Busy = false;
            if (failure is not null)
            {
                row.Status = failure;
                return;
            }

            row.Status = $"Connected as \"{name}\". {await TestAsync(service.Url, await _store.TokenAsync(name, CancellationToken.None).ConfigureAwait(true)).ConfigureAwait(true)}";
            row.Label = string.Empty;
            Refresh();
            return;
        }

        if (!row.ShowsToken)
        {
            row.ShowsToken = true;
            row.Status = string.Empty;
            return;
        }

        var token = row.Token.Trim();
        if (token.Length == 0)
        {
            row.Status = $"Paste the token you made on {service.Name} first.";
            return;
        }

        row.Busy = true;
        row.Status = "Checking the token…";
        var account = service.Id == "github" ? await GitHubLoginAsync(token).ConfigureAwait(true) : (Ok: true, Login: (string?)null, Problem: (string?)null);
        if (!account.Ok)
        {
            row.Busy = false;
            row.Status = account.Problem!;
            return;
        }

        // The same account again replaces its token; another account gets its own connection.
        var connectorName = account.Login is { } login && _store.All.Any(connector => connector.Name.Equals($"{service.Id}-{login}", StringComparison.OrdinalIgnoreCase))
            ? $"{service.Id}-{login}".ToLowerInvariant()
            : ConnectorCatalog.NameFor(service, account.Login ?? row.Label, taken);
        var check = await TestAsync(service.Url, token).ConfigureAwait(true);
        _store.Save(new Connector(connectorName, service.Url, ConnectorAuth.ApiKey), token);
        row.Busy = false;
        row.Token = row.Label = string.Empty;
        row.ShowsToken = false;
        row.Status = $"Connected {(account.Login is { } who ? $"{who}'s account " : string.Empty)}as \"{connectorName}\". {check}";
        Refresh();
    }

    [RelayCommand]
    private static void OpenTokenPage(CatalogRow row)
    {
        if (row.Service.TokenPage is { } page)
        {
            _ = OpenBrowser(new Uri(page));
        }
    }

    /// <summary>Whose GitHub token this is, or why it can't be used.</summary>
    private static async Task<(bool Ok, string? Login, string? Problem)> GitHubLoginAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Jarvis");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (false, null, "GitHub turned that token down. Check you copied all of it, and that it hasn't expired.");
            }

            response.EnsureSuccessStatusCode();
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            return (true, json.RootElement.GetProperty("login").GetString(), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            return (false, null, $"Couldn't check the token with GitHub: {exception.Message}");
        }
    }

    [RelayCommand]
    private void Remove(string name)
    {
        _store.Remove(name);
        Refresh();
    }

    [RelayCommand]
    private async Task TestRowAsync(string name)
    {
        if (Row(name) is not { } row || _store.All.FirstOrDefault(connector => connector.Name == name) is not { } connector)
        {
            return;
        }

        row.Status = "Checking…";
        row.Status = await TestAsync(connector.Url, await _store.TokenAsync(name, CancellationToken.None).ConfigureAwait(true)).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SignInRowAsync(string name)
    {
        if (Row(name) is not { } row || _store.All.FirstOrDefault(connector => connector.Name == name) is not { } connector)
        {
            return;
        }

        row.Status = "Sign in in the browser window that just opened…";
        row.Status = await SignInAsync(connector).ConfigureAwait(true) ?? "Signed in: jobs can use it.";
    }

    /// <summary>Runs the browser sign-in and saves the tokens; returns a message if it failed.</summary>
    private async Task<string?> SignInAsync(Connector connector)
    {
        try
        {
            var tokens = await _oauth.SignInAsync(connector.Url, OpenBrowser, CancellationToken.None).ConfigureAwait(true);
            _store.SaveSignIn(connector, tokens);
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return $"Sign-in didn't finish: {exception.Message}";
        }
    }

    private static ValueTask OpenBrowser(Uri page)
    {
        using var _ = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        return ValueTask.CompletedTask;
    }

    /// <summary>Sends an MCP initialize request and turns the answer into a sentence for the user.</summary>
    private static async Task<string> TestAsync(string url, string? token)
    {
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
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
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

    private ConnectorRow? Row(string name) => Connectors.FirstOrDefault(row => row.Name == name);

    private void Refresh()
    {
        Connectors.Clear();
        foreach (var connector in _store.All)
        {
            var auth = connector.Auth switch
            {
                ConnectorAuth.ApiKey => "Token (in Windows Credential Manager)",
                ConnectorAuth.SignIn => "Signed in with browser (tokens encrypted for your Windows account)",
                _ => "No sign-in",
            };
            var service = ConnectorCatalog.Of(connector);
            Connectors.Add(new ConnectorRow(connector.Name, service is null ? connector.Url : $"{service.Name} · {service.Description}", auth, connector.Auth == ConnectorAuth.SignIn));
        }

        foreach (var row in _catalog)
        {
            row.ActionLabel = _store.All.Any(connector => ConnectorCatalog.Of(connector) == row.Service) ? "Add another account" : "Connect";
        }

        Filter();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Filter()
    {
        var words = Search.Trim();
        Available.Clear();
        foreach (var row in _catalog.Where(row => words.Length == 0
                     || row.Service.Name.Contains(words, StringComparison.OrdinalIgnoreCase)
                     || row.Service.Category.Contains(words, StringComparison.OrdinalIgnoreCase)
                     || row.Service.Description.Contains(words, StringComparison.OrdinalIgnoreCase)))
        {
            Available.Add(row);
        }
    }
}

public sealed partial class ConnectorRow(string name, string url, string auth, bool canSignIn) : ObservableObject
{
    public string Name { get; } = name;

    public string Url { get; } = url;

    public string Auth { get; } = auth;

    public bool CanSignIn { get; } = canSignIn;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;
}

/// <summary>One service in the catalog, with its connect state while connecting.</summary>
public sealed partial class CatalogRow(CatalogService service) : ObservableObject
{
    public CatalogService Service => service;

    public string Name => service.Name;

    public string Detail => $"{service.Category} · {service.Description}";

    /// <summary>The service's first letter, for its badge.</summary>
    public string Initial => service.Name[..1];

    public bool UsesToken => service.SignIn == CatalogSignIn.Token;

    public string TokenHint => service.TokenHint ?? string.Empty;

    public string TokenPageLabel => $"Make a token on {service.Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLabel))]
    public partial string ActionLabel { get; set; } = "Connect";

    /// <summary>Asks which account it is once the service already has one.</summary>
    public bool ShowsLabel => ActionLabel != "Connect" && !UsesToken; // a token names its own account

    /// <summary>For another account: a short name for it ("work"), so its connection is easy to tell apart.</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowsToken { get; set; }

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = string.Empty;

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    public partial bool Busy { get; set; }
}
