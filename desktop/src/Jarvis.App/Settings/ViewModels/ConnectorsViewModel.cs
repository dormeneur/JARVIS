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
    private readonly Jarvis.Core.Google.GoogleCalendar _google;
    private readonly GitHubSignIn _github;

    public ConnectorsViewModel(IConnectorStore store, McpOAuth oauth, Jarvis.Core.Google.GoogleCalendar google, GitHubSignIn github)
    {
        _store = store;
        _oauth = oauth;
        _google = google;
        _github = github;
        GoogleSteps = [.. Jarvis.Core.Google.GoogleCalendar.SetupSteps.Select(step => new GoogleStep(step.Step, step.Url))];
        Refresh();
    }

    /// <summary>Making Jarvis's Google sign-in app, a step per link.</summary>
    public IReadOnlyList<GoogleStep> GoogleSteps { get; }

    public ObservableCollection<string> GoogleAccounts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GoogleNeedsApp))]
    public partial bool GoogleReady { get; set; }

    public bool GoogleNeedsApp => !GoogleReady;

    [ObservableProperty]
    public partial string GoogleClientId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GoogleClientSecret { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GoogleStatus { get; set; } = string.Empty;

    [RelayCommand]
    private void SaveGoogleApp()
    {
        var id = GoogleClientId.Trim();
        if (!id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || GoogleClientSecret.Trim().Length == 0)
        {
            GoogleStatus = "Paste both: the Client ID (it ends in .apps.googleusercontent.com) and the Client secret.";
            return;
        }

        _google.Accounts.SetApp(id, GoogleClientSecret);
        GoogleClientSecret = string.Empty;
        GoogleStatus = "Saved. Now connect your Google account.";
        Refresh();
    }

    [RelayCommand]
    private void ChangeGoogleApp() => GoogleReady = false;

    [RelayCommand]
    private async Task ConnectGoogleAsync()
    {
        GoogleStatus = "Sign in to Google in the browser window that just opened, and pick the account. Google may say the app isn't verified: it's your own app, so choose Advanced, then go to it.";
        try
        {
            var email = await _google.ConnectAsync(OpenBrowser, CancellationToken.None).ConfigureAwait(true);
            GoogleStatus = $"Connected {email}. \"Add … to my calendar\" now puts events straight into its Google Calendar.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            GoogleStatus = exception.Message.Contains("access_denied", StringComparison.Ordinal)
                ? "Google blocked it: the sign-in app is still in Testing. In Google Cloud, open Audience and press Publish app (or add your email under Test users), then connect again."
                : $"Google sign-in didn't finish: {exception.Message}";
        }

        Refresh();
    }

    [RelayCommand]
    private void RemoveGoogle(string email)
    {
        _google.Accounts.Remove(email);
        GoogleStatus = $"Removed {email}. To take Jarvis's access away on Google's side too, remove it at myaccount.google.com/connections.";
        Refresh();
    }

    [RelayCommand]
    private static void OpenGoogleStep(string url) => _ = OpenBrowser(new Uri(url));

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

        // GitHub, one click: approve Jarvis on github.com with a short code (the gh CLI's way).
        if (service.Id == "github" && GitHubSignIn.Available && !row.ShowsToken)
        {
            row.Busy = true;
            try
            {
                var code = await _github.StartAsync(CancellationToken.None).ConfigureAwait(true);
                CopyText(code.UserCode);
                row.Status = $"On GitHub, paste the code {code.UserCode} (it's copied) and press Authorize. Signed in to more than one GitHub account? Switch to the one you want there first.";
                _ = OpenBrowser(new Uri(code.VerificationUri));
                await SaveTokenAsync(row, await _github.WaitAsync(code, CancellationToken.None).ConfigureAwait(true), taken).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                row.Status = $"GitHub didn't connect: {exception.Message}";
            }
            finally
            {
                row.Busy = false;
            }

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
        try
        {
            await SaveTokenAsync(row, token, taken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            row.Status = $"Couldn't keep the token on this PC: {exception.Message}"; // said on the card, never a crash
        }
        finally
        {
            row.Busy = false;
        }
    }

    /// <summary>
    /// Keeps a token as a connection: checked with the service first (GitHub says whose it is, and
    /// the connection is named after that account), then tested, then saved. The same account again
    /// replaces its token; another account gets its own connection.
    /// </summary>
    private async Task SaveTokenAsync(CatalogRow row, string token, IReadOnlyCollection<string> taken)
    {
        var service = row.Service;
        row.Status = "Checking with " + service.Name + "…";
        var account = service.Id == "github" ? await GitHubLoginAsync(token).ConfigureAwait(true) : (Ok: true, Login: (string?)null, Problem: (string?)null);
        if (!account.Ok)
        {
            row.Status = account.Problem!;
            return;
        }

        // The same account again replaces its token; another account gets its own connection.
        var connectorName = account.Login is { } login && _store.All.Any(connector => connector.Name.Equals($"{service.Id}-{login}", StringComparison.OrdinalIgnoreCase))
            ? $"{service.Id}-{login}".ToLowerInvariant()
            : ConnectorCatalog.NameFor(service, account.Login ?? row.Label, taken);
        var check = await TestAsync(service.Url, token).ConfigureAwait(true);
        _store.Save(new Connector(connectorName, service.Url, ConnectorAuth.ApiKey), token);
        row.Token = row.Label = string.Empty;
        row.ShowsToken = false;
        row.Status = $"Connected {(account.Login is { } who ? $"{who}'s account " : string.Empty)}as \"{connectorName}\". {check}";
        Refresh();
    }

    private static void CopyText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
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

        GoogleReady = _google.Accounts.ClientId is not null;
        GoogleClientId = _google.Accounts.ClientId ?? GoogleClientId;
        GoogleAccounts.Clear();
        foreach (var email in _google.Accounts.Accounts)
        {
            GoogleAccounts.Add(email);
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

/// <summary>One step of making Jarvis's Google sign-in app, with its page in Google Cloud.</summary>
public sealed record GoogleStep(string Text, string Url);
