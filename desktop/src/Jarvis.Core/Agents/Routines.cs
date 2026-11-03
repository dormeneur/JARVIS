using System.Globalization;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Agents;

/// <summary>A background job that runs on a schedule while the PC is awake and Jarvis is running.</summary>
/// <param name="Schedule">See <see cref="RoutineSchedule"/>, e.g. "weekdays 09:00".</param>
/// <param name="Brief">What the job should do, as for any background job.</param>
public sealed record Routine(string Id, string Schedule, string Brief, DateTimeOffset Created, DateTimeOffset? LastRun);

/// <summary>Where routines are kept. Thread-safe.</summary>
public interface IRoutineStore
{
    IReadOnlyList<Routine> All { get; }

    void Add(Routine routine);

    void Remove(string id);

    void MarkRun(string id, DateTimeOffset at);
}

/// <summary>
/// Schedules in plain words: "daily 08:00", "weekdays 09:30", "monday 10:00" (any day name),
/// "every 2 hours", "every 30 minutes" (at least 15). Times are local, 24-hour.
/// </summary>
public static partial class RoutineSchedule
{
    private static readonly TimeSpan ShortestInterval = TimeSpan.FromMinutes(15);

    public static bool IsValid(string schedule) => Parse(schedule) is not null;

    /// <summary>The first run strictly after <paramref name="after"/>, in <paramref name="zone"/>'s local time.</summary>
    public static DateTimeOffset NextAfter(string schedule, DateTimeOffset after, TimeZoneInfo zone)
    {
        var rule = Parse(schedule) ?? throw new FormatException($"Not a schedule: \"{schedule}\".");
        if (rule.Interval is { } interval)
        {
            return after + interval;
        }

        var local = TimeZoneInfo.ConvertTime(after, zone);
        for (var day = 0; day <= 7; day++)
        {
            var date = local.Date.AddDays(day);
            var candidate = new DateTimeOffset(date + rule.TimeOfDay, zone.GetUtcOffset(date + rule.TimeOfDay));
            if (candidate > after && rule.Days.Contains(date.DayOfWeek))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unreachable: every rule matches some day within a week.");
    }

    /// <summary>Friendly text for lists: "Weekdays at 09:30", "Every 2 hours".</summary>
    public static string Describe(string schedule)
    {
        var text = schedule.Trim().ToLowerInvariant();
        if (Parse(text) is null)
        {
            return schedule;
        }

        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var head = char.ToUpperInvariant(text[0]) + text[1..space];
        return head == "Every" ? head + text[space..] : $"{head} at {text[(space + 1)..]}";
    }

    private static Rule? Parse(string schedule)
    {
        var text = schedule.Trim().ToLowerInvariant();
        if (Every().Match(text) is { Success: true } every)
        {
            var count = int.Parse(every.Groups["n"].Value, CultureInfo.InvariantCulture);
            var interval = every.Groups["unit"].Value.StartsWith('h') ? TimeSpan.FromHours(count) : TimeSpan.FromMinutes(count);
            return interval >= ShortestInterval ? new Rule([], default, interval) : null;
        }

        if (AtTime().Match(text) is not { Success: true } at
            || !TimeSpan.TryParseExact(at.Groups["time"].Value, @"h\:mm", CultureInfo.InvariantCulture, out var time)
            || time >= TimeSpan.FromDays(1))
        {
            return null;
        }

        DayOfWeek[]? days = at.Groups["days"].Value switch
        {
            "daily" => Enum.GetValues<DayOfWeek>(),
            "weekdays" => [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            var name => Enum.TryParse<DayOfWeek>(name, ignoreCase: true, out var day) ? [day] : null,
        };
        return days is null ? null : new Rule(days, time, null);
    }

    private sealed record Rule(DayOfWeek[] Days, TimeSpan TimeOfDay, TimeSpan? Interval);

    [GeneratedRegex(@"^every (?<n>\d{1,3}) (?<unit>hours?|minutes?)$")]
    private static partial Regex Every();

    [GeneratedRegex(@"^(?<days>[a-z]+) (?<time>\d{1,2}:\d{2})$")]
    private static partial Regex AtTime();
}

/// <summary>
/// Starts routines that are due. Each routine runs at most once per check, so a PC that slept
/// through several runs catches up with one, not a burst. Nothing runs while Jarvis is paused.
/// </summary>
public sealed class RoutineScheduler
{
    private readonly IRoutineStore _store;
    private readonly IAgentJobs _jobs;
    private readonly PauseSwitch _pause;
    private readonly TimeProvider _time;

    public RoutineScheduler(IRoutineStore store, IAgentJobs jobs, PauseSwitch pause, TimeProvider time)
    {
        _store = store;
        _jobs = jobs;
        _pause = pause;
        _time = time;
    }

    /// <summary>Call periodically (every 30 s is plenty).</summary>
    public void Tick()
    {
        if (_pause.IsPaused)
        {
            return;
        }

        var now = _time.GetUtcNow();
        foreach (var routine in _store.All.ToArray()) // running one changes the store
        {
            if (RoutineSchedule.IsValid(routine.Schedule)
                && now >= RoutineSchedule.NextAfter(routine.Schedule, routine.LastRun ?? routine.Created, _time.LocalTimeZone))
            {
                Run(routine, now);
            }
        }
    }

    /// <summary>Runs a routine now, whatever its schedule says.</summary>
    public void Run(Routine routine, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(routine);
        _store.MarkRun(routine.Id, now);
        _jobs.Start(routine.Brief);
    }
}
