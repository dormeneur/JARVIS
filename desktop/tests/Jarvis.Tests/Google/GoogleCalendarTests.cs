using System.Net;
using System.Text;
using Jarvis.Contracts.Calendar;
using Jarvis.Core.Agents;
using Jarvis.Core.Google;
using Jarvis.Core.Proactive;

namespace Jarvis.Tests.Google;

public sealed class GoogleCalendarTests
{
    private static readonly CalendarWeb Web = CalendarWeb.From("https://calendar.google.com/calendar/ical/asha%40example.test/private-x/basic.ics")!;

    [Fact]
    public void AddsWithTheAccountTheCalendarBelongsTo_ElseTheFirstSignedIn()
    {
        Assert.Equal("asha@example.test", Calendar(new FakeAccounts("other@example.test", "Asha@Example.test")).AccountFor(Web), StringComparer.OrdinalIgnoreCase);
        Assert.Equal("other@example.test", Calendar(new FakeAccounts("other@example.test")).AccountFor(Web));
        Assert.Null(Calendar(new FakeAccounts()).AccountFor(Web));
        Assert.Null(Calendar(new FakeAccounts("a@example.test")).AccountFor(CalendarWeb.From("https://outlook.live.com/owa/calendar/1/2/calendar.ics")!));
    }

    [Fact]
    public async Task PutsTheEventInTheMainCalendar_WithTheAccountsToken()
    {
        var handler = new Recording("""{"htmlLink": "https://calendar.google.com/event?eid=abc"}""");
        var calendar = new GoogleCalendar(new FakeAccounts("asha@example.test"), new McpOAuth(new HttpClient(), TimeProvider.System), new HttpClient(handler));
        var lunch = new CalendarEvent("Lunch with Sam", new DateTimeOffset(2030, 1, 15, 13, 0, 0, TimeSpan.FromHours(5.5)), new DateTimeOffset(2030, 1, 15, 14, 0, 0, TimeSpan.FromHours(5.5)), false, "Cafe", string.Empty);

        var page = await calendar.AddAsync("asha@example.test", lunch, TimeZoneInfo.Utc, CancellationToken.None);

        Assert.Equal("https://calendar.google.com/event?eid=abc", page);
        Assert.Equal("https://www.googleapis.com/calendar/v3/calendars/primary/events", handler.Url);
        Assert.Equal("Bearer token-for-asha@example.test", handler.Authorization);
        Assert.Contains("\"summary\":\"Lunch with Sam\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"dateTime\":\"2030-01-15T13:00:00+05:30\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"location\":\"Cafe\"", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("description", handler.Body, StringComparison.Ordinal); // no notes given
    }

    [Fact]
    public void AnAllDayEventIsADate()
    {
        var holiday = new CalendarEvent("Holiday", new DateTimeOffset(2030, 12, 25, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 12, 26, 0, 0, 0, TimeSpan.Zero), true, string.Empty, string.Empty);
        var body = GoogleCalendar.Body(holiday, TimeZoneInfo.Utc).ToJsonString();

        Assert.Contains("\"start\":{\"date\":\"2030-12-25\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"end\":{\"date\":\"2030-12-26\"}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANeedToSignInAgain_IsSaid_NotSent()
    {
        var calendar = new GoogleCalendar(new FakeAccounts { Expired = true }, new McpOAuth(new HttpClient(), TimeProvider.System), new HttpClient(new Recording("{}")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => calendar.AddAsync("a@example.test", new CalendarEvent("x", DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1), false, "", ""), TimeZoneInfo.Utc, CancellationToken.None));
        Assert.Contains("sign in to a@example.test again", error.Message, StringComparison.Ordinal);
    }

    private static GoogleCalendar Calendar(IGoogleAccounts accounts) => new(accounts, new McpOAuth(new HttpClient(), TimeProvider.System), new HttpClient());

    private sealed class FakeAccounts(params string[] emails) : IGoogleAccounts
    {
        public bool Expired { get; init; }

        public string? ClientId => "id.apps.googleusercontent.com";

        public IReadOnlyList<string> Accounts => emails;

        public void SetApp(string clientId, string clientSecret)
        {
        }

        public string? ClientSecret() => "secret";

        public void Save(string email, OAuthTokens tokens)
        {
        }

        public void Remove(string email)
        {
        }

        public Task<string?> TokenAsync(string email, CancellationToken cancellationToken) => Task.FromResult(Expired ? null : $"token-for-{email}");
    }

    private sealed class Recording(string reply) : HttpMessageHandler
    {
        public string? Url { get; private set; }

        public string? Authorization { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
