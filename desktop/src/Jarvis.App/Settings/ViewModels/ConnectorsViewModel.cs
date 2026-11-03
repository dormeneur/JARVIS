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
                ConnectorAuth.ApiKey => "API key (in Windows Credential Manager)",
                ConnectorAuth.SignIn => "Signed in with browser (tokens encrypted for your Windows account)",
                _ => "No sign-in",
            };
            Connectors.Add(new ConnectorRow(connector.Name, connector.Url, auth, connector.Auth == ConnectorAuth.SignIn));
        }

        OnPropertyChanged(nameof(IsEmpty));
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
