using Jarvis.Core;
using Jarvis.Core.Agents;

namespace Jarvis.Tests.Agents;

public sealed class RoutineTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // Monday 2 November 2026, 10:00 UTC.
    private static readonly DateTimeOffset Monday10 = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("daily 09:00", "2026-11-03T09:00:00+00:00")] // today's 09:00 has passed
    [InlineData("daily 11:30", "2026-11-02T11:30:00+00:00")]
    [InlineData("weekdays 09:00", "2026-11-03T09:00:00+00:00")]
    [InlineData("Friday 08:15", "2026-11-06T08:15:00+00:00")]
    [InlineData("monday 10:00", "2026-11-09T10:00:00+00:00")] // strictly after: next week
    [InlineData("every 2 hours", "2026-11-02T12:00:00+00:00")]
    [InlineData("every 30 minutes", "2026-11-02T10:30:00+00:00")]
    public void NextAfter_FindsTheNextSlot(string schedule, string expected)
        => Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), RoutineSchedule.NextAfter(schedule, Monday10, Utc));

    [Fact]
    public void Weekdays_SkipTheWeekend()
        => Assert.Equal(DayOfWeek.Monday, RoutineSchedule.NextAfter("weekdays 09:00", new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero), Utc).DayOfWeek);

    [Theory]
    [InlineData("every 5 minutes")] // too often
    [InlineData("daily 25:00")]
    [InlineData("someday 09:00")]
    [InlineData("tomorrow")]
    public void Nonsense_IsNotASchedule(string schedule) => Assert.False(RoutineSchedule.IsValid(schedule));

    [Theory]
    [InlineData("weekdays 09:30", "Weekdays at 09:30")]
    [InlineData("every 2 hours", "Every 2 hours")]
    public void Describe_IsReadable(string schedule, string expected) => Assert.Equal(expected, RoutineSchedule.Describe(schedule));

    [Fact]
    public void Scheduler_RunsWhenDue_OnceEvenAfterMissedSlots_AndNotWhilePaused()
    {
        var time = new Clock { Now = Monday10 };
        var store = new MemoryStore([new Routine("r1", "every 1 hour", "Check the news.", Monday10, null)]);
        var jobs = new FakeJobs();
        var pause = new PauseSwitch();
        var scheduler = new RoutineScheduler(store, jobs, pause, time);

        scheduler.Tick();
        Assert.Empty(jobs.Briefs); // not due yet

        time.Now += TimeSpan.FromHours(5); // slept through four slots
        scheduler.Tick();
        scheduler.Tick();
        Assert.Equal(["Check the news."], jobs.Briefs);

        time.Now += TimeSpan.FromHours(1);
        pause.Set(true);
        scheduler.Tick();
        Assert.Single(jobs.Briefs);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override TimeZoneInfo LocalTimeZone => Utc;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore(List<Routine> routines) : IRoutineStore
    {
        public IReadOnlyList<Routine> All => routines;

        public void Add(Routine routine) => routines.Add(routine);

        public void Remove(string id) => routines.RemoveAll(routine => routine.Id == id);

        public void MarkRun(string id, DateTimeOffset at)
        {
            var index = routines.FindIndex(routine => routine.Id == id);
            routines[index] = routines[index] with { LastRun = at };
        }
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
            return new(Guid.NewGuid(), brief, "folder", AgentJobStatus.Queued, DateTimeOffset.UnixEpoch);
        }

        public void Cancel(Guid id)
        {
        }
    }
}
