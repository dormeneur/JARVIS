using Jarvis.Contracts.Brain;
using Jarvis.Core.Proactive;

namespace Jarvis.Tests.Proactive;

public sealed class CalendarBrainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.FromHours(5.5)); // a Wednesday

    [Theory]
    [InlineData("what's on my calendar on 26th october", "2026-10-26", 1)] // a recent past date stays this year
    [InlineData("meetings on march 3", "2027-03-03", 1)]
    [InlineData("anything on 2026-12-01?", "2026-12-01", 1)]
    [InlineData("am I free on 5/11", "2026-11-05", 1)]
    [InlineData("what's on tomorrow", "2026-11-05", 1)]
    [InlineData("am I free on friday", "2026-11-06", 1)]
    [InlineData("meetings next week", "2026-11-09", 7)]
    [InlineData("what's on my calendar", "2026-11-04", 1)]
    public void WorksOutWhichDaysAQuestionMeans(string question, string firstDay, int days)
    {
        var (from, to, _) = CalendarBrain.Days(question, Now);

        Assert.Equal(firstDay, from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(days, (int)(to - from).TotalDays);
    }

    [Fact]
    public async Task ListsTheMeetings_OrSaysThereAreNone_OrThatNoCalendarIsConnected()
    {
        var time = new At(Now, TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST"));
        var meetings = new List<Meeting> { new("Standup", new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.FromHours(5.5)), null) };

        Assert.Equal("1 on your calendar on Monday, 26 October: 10:00 AM, Standup.", await Say(new CalendarBrain((_, _, _) => Task.FromResult<IReadOnlyList<Meeting>?>(meetings), time), "what's on 26th october"));
        Assert.Equal("Nothing on your calendar tomorrow.", await Say(new CalendarBrain((_, _, _) => Task.FromResult<IReadOnlyList<Meeting>?>([]), time), "meetings tomorrow"));
        Assert.StartsWith("No calendar is connected yet.", await Say(new CalendarBrain((_, _, _) => Task.FromResult<IReadOnlyList<Meeting>?>(null), time), "meetings tomorrow"), StringComparison.Ordinal);
    }

    [Fact]
    public void KnowsTheWebCalendarBehindAnIcalAddress()
    {
        var google = CalendarWeb.From("webcal://calendar.google.com/calendar/ical/asha%40example.test/private-abc/basic.ics")!;
        Assert.Equal(("Google Calendar", "asha@example.test"), (google.Name, google.Account));
        Assert.Equal("https://calendar.google.com/calendar/r/day/2026/10/26?authuser=asha%40example.test", google.Day(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)));
        Assert.Null(CalendarWeb.From("https://calendar.google.com/calendar/ical/abc123%40group.calendar.google.com/private-x/basic.ics")!.Account); // a shared calendar, not an account
        Assert.Equal("Outlook", CalendarWeb.From("https://outlook.live.com/owa/calendar/1/2/calendar.ics")!.Name);
        Assert.Null(CalendarWeb.From("http://insecure.example/cal.ics"));
        Assert.Null(CalendarWeb.From(""));
    }

    [Fact]
    public void ReadsAnEventFromTheBrain_OrGuessesItFromTheWords()
    {
        var parsed = CalendarDraft.Parse("""Sure: {"title": "Dentist", "date": "2026-11-10", "start": "09:30", "end": "10:15", "allDay": false, "location": "Clinic", "notes": ""}""", Now)!;
        Assert.Equal(("Dentist", 9, 30, 45.0, "Clinic"), (parsed.Title, parsed.Start.Hour, parsed.Start.Minute, (parsed.End - parsed.Start).TotalMinutes, parsed.Location));
        Assert.True(CalendarDraft.Parse("""{"title": "Holiday", "date": "2026-12-25", "start": null}""", Now)!.AllDay);
        Assert.Null(CalendarDraft.Parse("I couldn't do that", Now));

        var guessed = CalendarDraft.Guess("add lunch with Sam tomorrow at 1pm to my calendar", Now);
        Assert.Equal(("Lunch with Sam", 5, 13), (guessed.Title, guessed.Start.Day, guessed.Start.Hour));
        Assert.True(CalendarDraft.Guess("put dinner with 3 friends on friday on my calendar", Now).AllDay); // "3 friends" isn't a time
    }

    private static async Task<string> Say(CalendarBrain brain, string question)
    {
        var text = string.Empty;
        await foreach (var e in brain.RunTurnAsync(new BrainTurn(string.Empty, question, []), CancellationToken.None))
        {
            if (e is BrainEvent.TextDelta delta)
            {
                text += delta.Text;
            }
        }

        return text;
    }

    private sealed class At(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
