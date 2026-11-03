using System.Net;
using System.Runtime.CompilerServices;
using Jarvis.Contracts.Activity;
using Jarvis.Contracts.Brain;
using Jarvis.Contracts.Notch;
using Jarvis.Core;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Core.Proactive;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Tests.Agents;

public sealed class ProactiveTests : IDisposable
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private readonly string _folder = Directory.CreateTempSubdirectory("jarvis-proactive").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Calendar_ReadsZonesRepeatsExceptionsAndLinks()
    {
        const string Ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:standup
            SUMMARY:Standup
            DTSTART;TZID=America/New_York:20261102T090000
            RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=6
            EXDATE;TZID=America/New_York:20261104T090000
            LOCATION:https://meet.google.com/abc-defg-hij
            END:VEVENT
            BEGIN:VEVENT
            UID:standup
            RECURRENCE-ID;TZID=America/New_York:20261106T090000
            SUMMARY:Standup (moved)
            DTSTART;TZID=America/New_York:20261106T110000
            END:VEVENT
            BEGIN:VEVENT
            UID:lunch
            SUMMARY:Lunch\, with Sam
            DTSTART:20261103T120000Z
            DESCRIPTION:Join: https://us02web.zoom.us/j/123?pwd=x\nsee you
            END:VEVENT
            BEGIN:VEVENT
            UID:off
            SUMMARY:Holiday
            DTSTART;VALUE=DATE:20261103
            END:VEVENT
            BEGIN:VEVENT
            UID:gone
            SUMMARY:Cancelled
            STATUS:CANCELLED
            DTSTART:20261103T150000Z
            END:VEVENT
            BEGIN:VEVENT
            UID:long
            SUMMARY:Review of the
              quarter
            DTSTART;TZID=Europe/London:20261103T160000
            END:VEVENT
            END:VCALENDAR
            """;

        var meetings = IcsCalendar.Meetings(Ics, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero), London);

        Assert.Equal(
            [
                new Meeting("Standup", new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero), "https://meet.google.com/abc-defg-hij"),
                new Meeting("Lunch, with Sam", new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), "https://us02web.zoom.us/j/123?pwd=x"),
                new Meeting("Review of the quarter", new DateTimeOffset(2026, 11, 3, 16, 0, 0, TimeSpan.Zero), null),
                new Meeting("Standup (moved)", new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero), null),
                new Meeting("Standup", new DateTimeOffset(2026, 11, 9, 14, 0, 0, TimeSpan.Zero), "https://meet.google.com/abc-defg-hij"),
                new Meeting("Standup", new DateTimeOffset(2026, 11, 11, 14, 0, 0, TimeSpan.Zero), "https://meet.google.com/abc-defg-hij"),
            ],
            meetings); // the 4th was excluded, the 6th moved; COUNT=6 ends it after the 13th
    }

    [Fact]
    public void Calendar_KeepsWallClockAcrossDaylightSaving()
    {
        const string Ics = """
            BEGIN:VEVENT
            SUMMARY:Daily
            DTSTART;TZID=Europe/London:20261023T090000
            RRULE:FREQ=DAILY;UNTIL=20261026T000000Z
            END:VEVENT
            """;

        var starts = IcsCalendar.Meetings(Ics, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, London).Select(meeting => meeting.Start.UtcDateTime.Hour);

        Assert.Equal([8, 8, 9], starts); // 9:00 BST is 8:00 UTC; from 25 October 9:00 GMT
    }

    [Fact]
    public async Task HeadsUp_ComesOnceBeforeAMeeting_WithJoin()
    {
        var (coach, fakes) = Create(new ProactiveOptions { MorningBrief = false, CalendarAddress = "https://cal.example/basic.ics" });
        fakes.Ics = Event("Standup", fakes.Clock.Now + TimeSpan.FromMinutes(4.5), "https://meet.google.com/x");

        await coach.TickAsync(CancellationToken.None);
        await coach.TickAsync(CancellationToken.None);

        var (caption, spoken, actions) = Assert.Single(fakes.Said);
        Assert.Equal("Standup starts in 5 minutes.", caption);
        Assert.Equal(caption, spoken);
        Assert.Equal(["Join", "Dismiss"], actions.Select(action => action.Label));
    }

    [Fact]
    public async Task MorningBrief_OncePerDay_OnlyWhileTheUserIsThere_AndNeverOverTheNotch()
    {
        var (coach, fakes) = Create(new ProactiveOptions { CalendarAddress = "https://cal.example/basic.ics", Speak = false });
        fakes.Clock.Now = new DateTimeOffset(2026, 11, 3, 7, 0, 0, TimeSpan.Zero); // 7:00 in London
        fakes.Ics = Event("Standup", new DateTimeOffset(2026, 11, 3, 9, 30, 0, TimeSpan.Zero), null);

        fakes.Activity.Idle = TimeSpan.FromMinutes(30);
        await coach.TickAsync(CancellationToken.None);
        fakes.Activity.Idle = TimeSpan.Zero;
        fakes.Notch.Current = new NotchState.Listening();
        await coach.TickAsync(CancellationToken.None);
        Assert.Empty(fakes.Said); // away, then busy

        fakes.Notch.Current = NotchState.Idle;
        await coach.TickAsync(CancellationToken.None);
        await coach.TickAsync(CancellationToken.None);

        var (caption, spoken, _) = Assert.Single(fakes.Said);
        Assert.StartsWith("Good morning. You have one meeting today: Standup at ", caption, StringComparison.Ordinal);
        Assert.Null(spoken);

        // A restart the same morning doesn't repeat it; the next morning does.
        var (again, _) = Create(new ProactiveOptions { CalendarAddress = "https://cal.example/basic.ics" }, fakes);
        await again.TickAsync(CancellationToken.None);
        Assert.Single(fakes.Said);
        fakes.Clock.Now += TimeSpan.FromDays(1);
        await again.TickAsync(CancellationToken.None);
        Assert.Equal("Good morning. Your calendar is clear today.", fakes.Said[^1].Caption);
    }

    [Fact]
    public async Task Suggestion_NeedsActivity_AsksAFreshBrain_AndDoItStartsTheJob()
    {
        var (coach, fakes) = Create(new ProactiveOptions { MorningBrief = false, Suggestions = true, SuggestEveryMinutes = 30 });
        fakes.Reply = "SUGGEST: Want me to compare those laptops? | Compare the Dell XPS 14 and the Framework 13 for battery, weight and price.";
        fakes.Activity.Window = new ForegroundWindow("chrome", "Dell XPS 14 review - Google Chrome");

        for (var i = 0; i < 70; i++)
        {
            fakes.Clock.Now += TimeSpan.FromSeconds(30);
            await coach.TickAsync(CancellationToken.None);
        }

        var (caption, _, actions) = Assert.Single(fakes.Said); // 35 minutes: one check
        Assert.Equal("Want me to compare those laptops?", caption);
        Assert.Contains("chrome: Dell XPS 14 review", fakes.Requests.Single(), StringComparison.Ordinal);
        Assert.Equal(1, fakes.BrainsDisposed);
        Assert.Contains(fakes.Privacy.Recent(), look => look.Displays == 0);

        actions[0].Invoke();
        Assert.Equal(["Compare the Dell XPS 14 and the Framework 13 for battery, weight and price."], fakes.Jobs.Briefs);

        // The next check passes the offer along so it isn't repeated.
        fakes.Reply = "NONE";
        fakes.Clock.Now += TimeSpan.FromMinutes(31);
        await coach.TickAsync(CancellationToken.None);
        Assert.Contains("Want me to compare those laptops?", fakes.Requests[^1], StringComparison.Ordinal);
        Assert.Single(fakes.Said);
    }

    [Fact]
    public async Task NothingHappens_WhilePausedOrFullscreen()
    {
        var (coach, fakes) = Create(new ProactiveOptions { CalendarAddress = "https://cal.example/basic.ics" });
        fakes.Ics = Event("Standup", fakes.Clock.Now + TimeSpan.FromMinutes(2), null);

        fakes.Pause.Set(true);
        await coach.TickAsync(CancellationToken.None);
        fakes.Pause.Set(false);
        fakes.Activity.Fullscreen = true;
        await coach.TickAsync(CancellationToken.None);

        Assert.Empty(fakes.Said);
    }

    [Theory]
    [InlineData("NONE", false)]
    [InlineData("SUGGEST: Offer? | Brief.", true)]
    [InlineData("Sure!\nSUGGEST: Offer? | Brief.", true)]
    [InlineData("SUGGEST: no brief", false)]
    [InlineData("SUGGEST:  | Brief.", false)]
    public void ParseSuggestion(string reply, bool found)
        => Assert.Equal(found, ProactiveCoach.ParseSuggestion(reply) is not null);

    private static string Event(string title, DateTimeOffset start, string? link) => $"""
        BEGIN:VEVENT
        SUMMARY:{title}
        DTSTART:{start.UtcDateTime:yyyyMMdd'T'HHmmss}Z
        {(link is null ? string.Empty : "LOCATION:" + link)}
        END:VEVENT
        """;

    private (ProactiveCoach Coach, Fakes Fakes) Create(ProactiveOptions options, Fakes? reuse = null)
    {
        var fakes = reuse ?? new Fakes(_folder);
        var services = new ProactiveServices(
            fakes.Activity,
            new CalendarFeed(new HttpClient(new IcsHandler(fakes)), fakes.Clock),
            fakes.Jobs,
            fakes.Notch,
            () => new FakeBrain(fakes),
            fakes.Privacy,
            fakes.Pause,
            (caption, spoken, actions) =>
            {
                fakes.Said.Add((caption, spoken, actions));
                return Task.CompletedTask;
            });
        return (new ProactiveCoach(services, new Monitor(options), fakes.Clock, NullLogger<ProactiveCoach>.Instance, Path.Combine(_folder, "morning.txt")), fakes);
    }

    private sealed class Fakes(string folder)
    {
        public Clock Clock { get; } = new() { Now = new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero) };

        public Activity Activity { get; } = new();

        public Notch Notch { get; } = new();

        public FakeJobs Jobs { get; } = new();

        public PauseSwitch Pause { get; } = new();

        public ScreenPrivacy Privacy { get; } = new(new PrivacyMonitor(), Path.Combine(folder, "looks.log"), TimeProvider.System);

        public List<(string Caption, string? Spoken, IReadOnlyList<NotchAction> Actions)> Said { get; } = [];

        public string Ics { get; set; } = string.Empty;

        public string Reply { get; set; } = "NONE";

        public List<string> Requests { get; } = [];

        public int BrainsDisposed { get; set; }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override TimeZoneInfo LocalTimeZone => London;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Activity : IActivityWatch
    {
        public ForegroundWindow? Window { get; set; }

        public TimeSpan Idle { get; set; }

        public bool Fullscreen { get; set; }

        public bool InFullscreen => Fullscreen;

        public ForegroundWindow? Foreground() => Window;

        public TimeSpan IdleFor() => Idle;
    }

    private sealed class Notch : INotchPresenter
    {
        public NotchState Current { get; set; } = NotchState.Idle;

        public void Show(NotchState state) => Current = state;
    }

    private sealed class FakeJobs : IAgentJobs
    {
        public List<string> Briefs { get; } = [];

        public IReadOnlyList<AgentJobInfo> Jobs => [];

        public event Action<AgentJobInfo>? Changed
        {
            add { }
            remove { }
        }

        public AgentJobInfo Start(string brief)
        {
            Briefs.Add(brief);
            return new AgentJobInfo(Guid.NewGuid(), brief, string.Empty, AgentJobStatus.Queued, DateTimeOffset.UnixEpoch);
        }

        public void Cancel(Guid id)
        {
        }
    }

    private sealed class FakeBrain(Fakes fakes) : IAssistantBrain, IAsyncDisposable
    {
        public async IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            fakes.Requests.Add(turn.UserText);
            await Task.Yield();
            yield return new BrainEvent.TextDelta(fakes.Reply);
            yield return new BrainEvent.Completed();
        }

        public ValueTask DisposeAsync()
        {
            fakes.BrainsDisposed++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IcsHandler(Fakes fakes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fakes.Ics) });
    }

    private sealed class Monitor(ProactiveOptions value) : IOptionsMonitor<ProactiveOptions>
    {
        public ProactiveOptions CurrentValue => value;

        public ProactiveOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ProactiveOptions, string?> listener) => null;
    }

    private sealed class PrivacyMonitor : IOptionsMonitor<PrivacyOptions>
    {
        public PrivacyOptions CurrentValue { get; } = new();

        public PrivacyOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<PrivacyOptions, string?> listener) => null;
    }
}
