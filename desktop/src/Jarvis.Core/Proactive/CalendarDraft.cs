using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts.Calendar;

namespace Jarvis.Core.Proactive;

/// <summary>
/// Turns "add lunch with Sam tomorrow at 1pm" into an event for the user to check: the assistant's
/// brain reads the request (<see cref="Instructions"/>, <see cref="Parse"/>); if its reply can't be
/// used, <see cref="Guess"/> reads the date and time itself. Either way the user sees it before
/// anything is added.
/// </summary>
public static partial class CalendarDraft
{
    /// <summary>What the brain is told; it replies with one JSON object and nothing else.</summary>
    public static string Instructions(DateTimeOffset now) =>
        $$"""
        Turn the user's request into one calendar event. Now is {{now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}} (UTC{{now.ToString("zzz", CultureInfo.InvariantCulture)}}).
        Reply with only this JSON object, no other text:
        {"title": "short event title", "date": "YYYY-MM-DD", "start": "HH:mm or null", "end": "HH:mm or null", "allDay": false, "location": "", "notes": ""}
        Use null start for an all-day event. Without an end, leave it null. Never invent a location or notes the user didn't give.
        """;

    /// <summary>The brain's JSON reply as an event (an hour long unless it says), or null when it isn't usable.</summary>
    public static CalendarEvent? Parse(string reply, DateTimeOffset now)
    {
        var start = reply.IndexOf('{', StringComparison.Ordinal);
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = json.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            if (Text("title") is not { Length: > 0 } title
                || !DateTime.TryParseExact(Text("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return null;
            }

            var day = new DateTimeOffset(date, now.Offset);
            var allDay = (root.TryGetProperty("allDay", out var flag) && flag.ValueKind == JsonValueKind.True) || Time(Text("start")) is null;
            if (allDay)
            {
                return new CalendarEvent(title, day, day.AddDays(1), true, Text("location") ?? string.Empty, Text("notes") ?? string.Empty);
            }

            var from = day + Time(Text("start"))!.Value;
            var to = Time(Text("end")) is { } until && day + until > from ? day + until : from.AddHours(1);
            return new CalendarEvent(title, from, to, false, Text("location") ?? string.Empty, Text("notes") ?? string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Without the brain: the day the request names (as calendar questions read it), a time like
    /// "at 1pm" or "15:30", and the rest of the words as the title.
    /// </summary>
    public static CalendarEvent Guess(string request, DateTimeOffset now)
    {
        var (day, _, _) = CalendarBrain.Days(request, now);
        var match = ClockTime().Match(request);
        var title = Filler().Replace(ClockTime().Replace(request, " "), " ");
        title = Regex.Replace(title, @"\s+", " ").Trim(' ', ',', '.');
        title = title.Length == 0 ? "New event" : char.ToUpper(title[0], CultureInfo.InvariantCulture) + title[1..];
        if (!match.Success)
        {
            return new CalendarEvent(title, day, day.AddDays(1), true, string.Empty, string.Empty);
        }

        string Part(params int[] groups) => groups.Select(group => match.Groups[group].Value).FirstOrDefault(value => value.Length > 0) ?? string.Empty;
        var hour = int.Parse(Part(1, 4, 7), CultureInfo.InvariantCulture);
        var minute = Part(2, 5) is { Length: > 0 } minutes ? int.Parse(minutes, CultureInfo.InvariantCulture) : 0;
        var half = Part(3, 6, 8).ToLowerInvariant();
        hour = half.StartsWith('p') && hour < 12 ? hour + 12 : half.StartsWith('a') && hour == 12 ? 0 : hour;
        var start = day + new TimeSpan(Math.Min(hour, 23), Math.Min(minute, 59), 0);
        return new CalendarEvent(title, start, start.AddHours(1), false, string.Empty, string.Empty);
    }

    /// <summary>An all-day or timed event as one line, for the user to hear.</summary>
    public static string Describe(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var text = new StringBuilder($"\"{e.Title}\" on {e.Start.ToString("dddd, d MMMM", CultureInfo.InvariantCulture)}");
        if (!e.AllDay)
        {
            text.Append(CultureInfo.InvariantCulture, $", {e.Start.ToString("h:mm tt", CultureInfo.InvariantCulture)} to {e.End.ToString("h:mm tt", CultureInfo.InvariantCulture)}");
        }

        return text.ToString();
    }

    private static TimeSpan? Time(string? text)
        => TimeSpan.TryParseExact(text, ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var time) ? time : null;

    // "at 1", "at 1:30 pm", "15:30", "1pm": a bare number ("with 3 people") isn't a time.
    [GeneratedRegex(@"\bat\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b|\b(\d{1,2}):(\d{2})\s*(am|pm)?\b|\b(\d{1,2})\s*(am|pm)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"\b(please|can you|could you|jarvis|add|put|schedule|create|book|set up|an?|event|meeting for|to my calendar|on my calendar|in my calendar|my calendar|calendar|today|tomorrow|on\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)|next week|on\s+the\s+\d{1,2}(st|nd|rd|th)?|on\s+\d{1,2}(st|nd|rd|th)?\s+\w+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();
}
