using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Jarvis.Contracts.Brain;

namespace Jarvis.Core.Proactive;

/// <summary>
/// Answers "what's on my calendar…" from the user's iCal feed, on this PC: no screenshot, no model.
/// Works out which days the question means (today, tomorrow, Friday, next week, 26th October…),
/// and lists the meetings in them.
/// </summary>
/// <param name="meetings">The feed's meetings between two times, or null when no calendar is connected.</param>
public sealed partial class CalendarBrain(Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<Meeting>?>> meetings, TimeProvider time) : IAssistantBrain
{
    public async IAsyncEnumerable<BrainEvent> RunTurnAsync(BrainTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var local = time.LocalTimeZone;
        var (from, to, said) = Days(turn.UserText, TimeZoneInfo.ConvertTime(time.GetUtcNow(), local));
        string answer;
        try
        {
            answer = await meetings(from, to, cancellationToken).ConfigureAwait(false) is { } found
                ? Describe(found, said, local)
                : "No calendar is connected yet. Paste your calendar's secret iCal address in Settings → Proactive.";
        }
        catch (HttpRequestException)
        {
            answer = "I couldn't reach your calendar just now. Check your internet connection and ask again.";
        }

        yield return new BrainEvent.TextDelta(answer);
        yield return new BrainEvent.Completed();
    }

    /// <summary>The days a question means, in the user's time zone, and how to say them ("on Sunday, 26 October").</summary>
    internal static (DateTimeOffset From, DateTimeOffset To, string Said) Days(string question, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.Date, now.Offset);
        var text = question.ToLowerInvariant();
        if (ExplicitDate(text, today) is { } date)
        {
            return (date, date.AddDays(1), "on " + date.ToString("dddd, d MMMM", CultureInfo.InvariantCulture));
        }

        if (text.Contains("tomorrow", StringComparison.Ordinal))
        {
            return (today.AddDays(1), today.AddDays(2), "tomorrow");
        }

        if (text.Contains("yesterday", StringComparison.Ordinal))
        {
            return (today.AddDays(-1), today, "yesterday");
        }

        if (NextWeek().IsMatch(text))
        {
            var monday = today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7));
            return (monday, monday.AddDays(7), "next week");
        }

        if (ThisWeek().IsMatch(text))
        {
            return (today, today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7)), "for the rest of this week");
        }

        if (Weekday().Match(text) is { Success: true } weekday)
        {
            var wanted = Enum.Parse<DayOfWeek>(weekday.Groups[1].Value, ignoreCase: true);
            var ahead = ((int)wanted - (int)today.DayOfWeek + 7) % 7;
            var day = today.AddDays(ahead);
            return (day, day.AddDays(1), "on " + day.ToString("dddd, d MMMM", CultureInfo.InvariantCulture));
        }

        return (today, today.AddDays(1), "today");
    }

    /// <summary>"26th october", "october 26", "26 oct 2026", "26/10", "2026-10-26"; a date without a year is the coming one, or a recent past one.</summary>
    private static DateTimeOffset? ExplicitDate(string text, DateTimeOffset today)
    {
        int day, month, year = 0;
        if (Iso().Match(text) is { Success: true } iso)
        {
            (year, month, day) = (int.Parse(iso.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(iso.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        else if (DayMonth().Match(text) is { Success: true } dm && MonthOf(dm.Groups[2].Value) is { } m1)
        {
            (day, month) = (int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture), m1);
            year = dm.Groups[3].Success ? int.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        }
        else if (MonthDay().Match(text) is { Success: true } md && MonthOf(md.Groups[1].Value) is { } m2)
        {
            (day, month) = (int.Parse(md.Groups[2].Value, CultureInfo.InvariantCulture), m2);
            year = md.Groups[3].Success ? int.Parse(md.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        }
        else if (Numeric().Match(text) is { Success: true } numeric)
        {
            // ponytail: day/month order, as written in most of the world (and on this user's forms).
            (day, month) = (int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(numeric.Groups[2].Value, CultureInfo.InvariantCulture));
        }
        else
        {
            return null;
        }

        if (year == 0)
        {
            // No year: this year's, unless that was more than a month ago (then next year's).
            year = today.Year;
            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                return null;
            }

            if (new DateTime(year, month, day) < today.Date.AddMonths(-1))
            {
                year++;
            }
        }

        return month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateTimeOffset(new DateTime(year, month, day), today.Offset)
            : null;
    }

    private static int? MonthOf(string word)
    {
        var at = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames, name => name.Length > 0 && word.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? at + 1 : null;
    }

    internal static string Describe(IReadOnlyList<Meeting> found, string said, TimeZoneInfo local)
    {
        if (found.Count == 0)
        {
            return $"Nothing on your calendar {said}.";
        }

        var text = new StringBuilder($"{found.Count} on your calendar {said}: ");
        var days = found.Select(m => TimeZoneInfo.ConvertTime(m.Start, local).Date).Distinct().Count();
        text.AppendJoin("; ", found.OrderBy(m => m.Start).Select(m =>
        {
            var start = TimeZoneInfo.ConvertTime(m.Start, local);
            var when = days > 1 ? start.ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture) : start.ToString("h:mm tt", CultureInfo.InvariantCulture);
            return $"{when}, {m.Title}";
        }));
        return text.Append('.').ToString();
    }

    [GeneratedRegex(@"\b(\d{4})-(\d{1,2})-(\d{1,2})\b")]
    private static partial Regex Iso();

    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?(?:,?\s+(\d{4}))?\b")]
    private static partial Regex DayMonth();

    [GeneratedRegex(@"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(\d{4}))?\b")]
    private static partial Regex MonthDay();

    [GeneratedRegex(@"\b(\d{1,2})[/.](\d{1,2})\b")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"\bnext week\b")]
    private static partial Regex NextWeek();

    [GeneratedRegex(@"\b(this|rest of the) week\b")]
    private static partial Regex ThisWeek();

    [GeneratedRegex(@"\b(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b")]
    private static partial Regex Weekday();
}
