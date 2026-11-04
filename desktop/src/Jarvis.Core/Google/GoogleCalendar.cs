using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts.Calendar;
using Jarvis.Core.Agents;

namespace Jarvis.Core.Google;

/// <summary>
/// The Google accounts Jarvis is signed in to, with the sign-in app the user made for Jarvis in
/// Google Cloud (Google doesn't let apps register themselves). Tokens are kept protected and
/// refreshed without the user. Thread-safe.
/// </summary>
public interface IGoogleAccounts
{
    /// <summary>The user's Google sign-in app for Jarvis; null until it's set up.</summary>
    string? ClientId { get; }

    /// <summary>Signed-in accounts, by email.</summary>
    IReadOnlyList<string> Accounts { get; }

    /// <summary>Keeps the sign-in app (the secret in Windows-protected storage).</summary>
    void SetApp(string clientId, string clientSecret);

    string? ClientSecret();

    void Save(string email, OAuthTokens tokens);

    void Remove(string email);

    /// <summary>A fresh access token for the account, or null when it needs signing in again.</summary>
    Task<string?> TokenAsync(string email, CancellationToken cancellationToken);
}

/// <summary>
/// Signs in to Google as Jarvis (the user's own sign-in app, PKCE, loopback redirect, offline
/// access so it keeps working), and adds events to Google Calendar. Scopes are only what's used:
/// the account's email (to tell accounts apart) and creating calendar events.
/// </summary>
public sealed class GoogleCalendar(IGoogleAccounts accounts, McpOAuth oauth, HttpClient http)
{
    private static readonly JsonSerializerOptions Plain = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string Scope = "openid email https://www.googleapis.com/auth/calendar.events";

    /// <summary>Where each step of making Jarvis's sign-in app is, in Google Cloud.</summary>
    public static IReadOnlyList<(string Step, string Url)> SetupSteps { get; } =
    [
        ("1. Make a project (any name, e.g. Jarvis)", "https://console.cloud.google.com/projectcreate"),
        ("2. Turn on the Google Calendar API", "https://console.cloud.google.com/apis/library/calendar-json.googleapis.com"),
        ("3. Set up the sign-in screen: choose External, then on Audience press Publish app (sign-ins then don't expire every 7 days)", "https://console.cloud.google.com/auth/branding"),
        ("4. Create a client: type Desktop app, then copy its Client ID and Client secret here", "https://console.cloud.google.com/auth/clients/create"),
    ];

    public IGoogleAccounts Accounts => accounts;

    /// <summary>Signs in one more Google account; returns its email.</summary>
    public async Task<string> ConnectAsync(Func<Uri, ValueTask> openBrowser, CancellationToken cancellationToken)
    {
        if (accounts.ClientId is not { } clientId)
        {
            throw new InvalidOperationException("Set up Jarvis's Google sign-in app first.");
        }

        var tokens = await oauth.SignInWithAppAsync(
            "https://accounts.google.com/o/oauth2/v2/auth",
            "https://oauth2.googleapis.com/token",
            clientId,
            accounts.ClientSecret(),
            Scope,
            new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent select_account" },
            openBrowser,
            cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var email = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false))?["email"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Google didn't say which account signed in.");
        accounts.Save(email, tokens);
        return email;
    }

    /// <summary>
    /// The account to add events with for this calendar: the one its iCal address belongs to when
    /// that's signed in, else the first signed-in account; null when none is (or it isn't Google).
    /// </summary>
    public string? AccountFor(Jarvis.Core.Proactive.CalendarWeb web)
    {
        ArgumentNullException.ThrowIfNull(web);
        var signedIn = accounts.Accounts;
        if (web.Name != "Google Calendar" || signedIn.Count == 0)
        {
            return null;
        }

        return signedIn.FirstOrDefault(email => string.Equals(email, web.Account, StringComparison.OrdinalIgnoreCase)) ?? signedIn[0];
    }

    /// <summary>Adds the event to the account's main calendar; returns its page in Google Calendar.</summary>
    public async Task<string> AddAsync(string account, CalendarEvent e, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(zone);
        var token = await accounts.TokenAsync(account, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Jarvis needs to sign in to {account} again (Settings → Connectors).");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/calendar/v3/calendars/primary/events")
        {
            Content = new StringContent(Body(e, zone).ToJsonString(Plain), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Calendar didn't add it ({(int)response.StatusCode}): {(text.Length > 200 ? text[..200] : text)}");
        }

        return JsonNode.Parse(text)?["htmlLink"]?.GetValue<string>() ?? "https://calendar.google.com/calendar/r";
    }

    /// <summary>The Calendar API's event: a date for all-day, else times with the user's time zone.</summary>
    internal static JsonObject Body(CalendarEvent e, TimeZoneInfo zone)
    {
        JsonObject When(DateTimeOffset time)
        {
            if (e.AllDay)
            {
                return new JsonObject { ["date"] = time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            }

            // The time carries its offset; the zone's IANA name (when Windows has one) keeps repeats right.
            var when = new JsonObject { ["dateTime"] = time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) };
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana))
            {
                when["timeZone"] = iana;
            }

            return when;
        }

        var body = new JsonObject { ["summary"] = e.Title, ["start"] = When(e.Start), ["end"] = When(e.End) };
        if (e.Location.Length > 0)
        {
            body["location"] = e.Location;
        }

        if (e.Notes.Length > 0)
        {
            body["description"] = e.Notes;
        }

        return body;
    }
}
