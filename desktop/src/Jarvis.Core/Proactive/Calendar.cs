using System.Globalization;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Proactive;

/// <summary>One meeting on the user's calendar.</summary>
/// <param name="Link">A video-call link found in the event, if any.</param>
public sealed record Meeting(string Title, DateTimeOffset Start, string? Link);

/// <summary>
/// Reads an iCalendar (.ics) feed, such as Google Calendar's or Outlook's "secret address", into
/// the meetings in a time window. Handles time zones, all-day events (skipped: they aren't
/// meetings), cancelled events, moved or cancelled single occurrences, and the common repeat rules
/// (daily, weekly on given days, monthly on the same date; interval, count, until).
/// </summary>
public static partial class IcsCalendar
{
    private const int MaximumOccurrences = 5000; // per series, to stop runaway rules

    public static IReadOnlyList<Meeting> Meetings(string ics, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo local)
    {
        ArgumentNullException.ThrowIfNull(ics);
        ArgumentNullException.ThrowIfNull(local);
        var events = Events(Unfold(ics)).ToList();

        // A RECURRENCE-ID event replaces (or, if cancelled, removes) one occurrence of its series.
        var replaced = events.Where(e => e.RecurrenceId is not null)
            .Select(e => (e.Uid, Start: Time(e.RecurrenceId!, local)))
            .Where(key => key.Start is not null)
            .ToHashSet();

        var meetings = new List<Meeting>();
        foreach (var e in events)
        {
            if (e.Cancelled || Time(e.Start, local) is not { } start)
            {
                continue;
            }

            var link = Link(e);
            if (e.RecurrenceId is not null || e.Rule is null)
            {
                if (start >= from && start < to)
                {
                    meetings.Add(new Meeting(e.Title, start, link));
                }

                continue;
            }

            var skipped = e.Excluded.Select(value => Time(value, local)).OfType<DateTimeOffset>().ToHashSet();
            foreach (var occurrence in Occurrences(e.Start, e.Rule, local, to))
            {
                if (occurrence >= from && !skipped.Contains(occurrence) && !replaced.Contains((e.Uid, occurrence)))
                {
                    meetings.Add(new Meeting(e.Title, occurrence, link));
                }
            }
        }

        return [.. meetings.OrderBy(meeting => meeting.Start)];
    }

    /// <summary>Long lines are folded onto continuation lines that start with a space or tab.</summary>
    private static List<string> Unfold(string ics)
    {
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t' && lines.Count > 0)
            {
                lines[^1] += raw[1..];
            }
            else
            {
                lines.Add(raw);
            }
        }

        return lines;
    }

    private static IEnumerable<Event> Events(List<string> lines)
    {
        Event? current = null;
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT")
            {
                current = new Event();
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line == "END:VEVENT")
            {
                yield return current;
                current = null;
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var head = line[..colon];
            var value = line[(colon + 1)..];
            var name = head.Split(';')[0].ToUpperInvariant();
            var property = new Property(head, value);
            switch (name)
            {
                case "UID": current.Uid = value; break;
                case "SUMMARY": current.Title = Unescape(value); break;
                case "DTSTART": current.Start = property; break;
                case "RRULE": current.Rule = value; break;
                case "EXDATE":
                    current.Excluded.AddRange(value.Split(',').Select(part => property with { Value = part }));
                    break;
                case "RECURRENCE-ID": current.RecurrenceId = property; break;
                case "STATUS": current.Cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                case "URL" or "LOCATION" or "DESCRIPTION" or "X-GOOGLE-CONFERENCE": current.Text.Add(Unescape(value)); break;
            }
        }
    }

    /// <summary>A date-time property as an instant; null for all-day dates or anything unreadable.</summary>
    private static DateTimeOffset? Time(Property property, TimeZoneInfo local)
    {
        if (property.Head.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !property.Head.Contains("VALUE=DATE-TIME", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = property.Value.Trim();
        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            return null;
        }

        return utc ? new DateTimeOffset(wall, TimeSpan.Zero) : InZone(wall, Zone(property, local));
    }

    private static TimeZoneInfo Zone(Property property, TimeZoneInfo local)
    {
        var tzid = TzId().Match(property.Head);
        if (tzid.Success)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(tzid.Groups[1].Value.Trim('"'));
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // An unknown zone name: treat the time as local.
            }
        }

        return local;
    }

    private static DateTimeOffset InZone(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1); // in the spring-forward gap
        }

        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    /// <summary>Every start of a repeating event up to <paramref name="to"/>, in the event's own wall clock (so DST is right).</summary>
    private static IEnumerable<DateTimeOffset> Occurrences(Property start, string rule, TimeZoneInfo local, DateTimeOffset to)
    {
        var parts = rule.Split(';').Select(part => part.Split('=', 2)).Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].ToUpperInvariant(), pair => pair[1], StringComparer.Ordinal);
        if (Time(start, local) is not { } first)
        {
            yield break;
        }

        var zone = start.Value.EndsWith('Z') ? TimeZoneInfo.Utc : Zone(start, local);
        var wall = TimeZoneInfo.ConvertTime(first, zone).DateTime;
        var interval = parts.TryGetValue("INTERVAL", out var i) && int.TryParse(i, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 1;
        var count = parts.TryGetValue("COUNT", out var c) && int.TryParse(c, CultureInfo.InvariantCulture, out var limit) ? limit : int.MaxValue;
        var until = parts.TryGetValue("UNTIL", out var u) ? Time(new Property("UNTIL", u.Length == 8 ? u + "T235959" : u), local) : null;
        var days = parts.TryGetValue("BYDAY", out var byDay)
            ? byDay.Split(',').Select(Weekday).OfType<DayOfWeek>().ToHashSet()
            : [wall.DayOfWeek];

        IEnumerable<DateTime> Candidates()
        {
            for (var step = 0; step < MaximumOccurrences; step++)
            {
                switch (parts.GetValueOrDefault("FREQ"))
                {
                    case "DAILY":
                        yield return wall.AddDays(step * interval);
                        break;
                    case "WEEKLY":
                        var weekStart = wall.AddDays((-(int)wall.DayOfWeek) + (7 * step * interval));
                        for (var d = 0; d < 7; d++)
                        {
                            var day = weekStart.AddDays(d);
                            if (days.Contains(day.DayOfWeek) && day >= wall)
                            {
                                yield return day;
                            }
                        }

                        break;
                    case "MONTHLY" when !parts.ContainsKey("BYDAY"):
                        var month = wall.AddMonths(step * interval);
                        if (month.Day == wall.Day)
                        {
                            yield return month; // a 31st skips shorter months, as calendars do
                        }

                        break;
                    default:
                        yield break; // a rule we don't read: better silent than wrong
                }
            }
        }

        var emitted = 0;
        foreach (var candidate in Candidates())
        {
            var at = InZone(candidate, zone);
            if (emitted++ >= count || at > to || (until is not null && at > until))
            {
                yield break;
            }

            yield return at;
        }
    }

    private static DayOfWeek? Weekday(string code) => code.Length >= 2 ? code[^2..].ToUpperInvariant() switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => null,
    } : null;

    private static string? Link(Event e)
    {
        var call = e.Text.Select(text => CallLink().Match(text)).FirstOrDefault(match => match.Success);
        return call?.Value;
    }

    private static string Unescape(string value)
        => value.Replace("\\n", " ", StringComparison.OrdinalIgnoreCase).Replace("\\,", ",", StringComparison.Ordinal).Replace("\\;", ";", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal).Trim();

    [GeneratedRegex(@"TZID=(""[^""]+""|[^;:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TzId();

    [GeneratedRegex(@"https://[^\s""<>]*(zoom\.us|meet\.google\.com|teams\.microsoft\.com|teams\.live\.com|webex\.com|whereby\.com|around\.co)[^\s""<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex CallLink();

    private sealed record Property(string Head, string Value);

    private sealed class Event
    {
        public string Uid { get; set; } = string.Empty;

        public string Title { get; set; } = "Meeting";

        public Property Start { get; set; } = new("DTSTART", string.Empty);

        public string? Rule { get; set; }

        public Property? RecurrenceId { get; set; }

        public bool Cancelled { get; set; }

        public List<Property> Excluded { get; } = [];

        public List<string> Text { get; } = [];
    }
}

/// <summary>
/// The user's calendar feed, fetched at most every 15 minutes. A failed fetch keeps the last good
/// copy, so a flaky network doesn't make meetings vanish.
/// </summary>
public sealed class CalendarFeed(HttpClient http, TimeProvider time)
{
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(15);
    private string? _url;
    private string? _ics;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    /// <summary>Meetings from an hour ago to a day ahead; none when no address is set.</summary>
    public async Task<IReadOnlyList<Meeting>> UpcomingAsync(string address, TimeZoneInfo local, CancellationToken cancellationToken)
    {
        var url = address.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url["webcal://".Length..];
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return [];
        }

        var now = time.GetUtcNow();
        if (url != _url || now - _fetchedAt > Refresh)
        {
            _fetchedAt = now; // also after a failure, so a dead address isn't hammered
            if (url != _url)
            {
                _ics = null;
                _url = url;
            }

            _ics = await http.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
        }

        return _ics is null ? [] : IcsCalendar.Meetings(_ics, now - TimeSpan.FromHours(1), now + TimeSpan.FromDays(1), local);
    }
}
