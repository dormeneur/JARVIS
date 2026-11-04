using System.Globalization;
using Jarvis.Contracts.Calendar;

namespace Jarvis.Core.Proactive;

/// <summary>
/// The web calendar behind an iCal address: where to open a day in it, and its own "new event"
/// page filled in with an event (an iCal address is read-only, so that page is how an event goes
/// in until Jarvis signs in to the calendar itself). Google opens on the right account.
/// </summary>
public sealed record CalendarWeb(string Name, string Home, string? Account)
{
    /// <summary>The web calendar for an iCal address; null when there's no usable address.</summary>
    public static CalendarWeb? From(string? icalAddress)
    {
        var url = (icalAddress ?? string.Empty).Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url["webcal://".Length..];
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host == "calendar.google.com")
        {
            // /calendar/ical/<calendar id, the account's address for the main calendar>/private-…/basic.ics
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var id = parts.Length > 2 && parts[1] == "ical" ? Uri.UnescapeDataString(parts[2]) : null;
            return new CalendarWeb("Google Calendar", "https://calendar.google.com/calendar/r", id is not null && id.Contains('@', StringComparison.Ordinal) && !id.EndsWith(".calendar.google.com", StringComparison.Ordinal) ? id : null);
        }

        if (host.StartsWith("outlook.", StringComparison.Ordinal) || host.EndsWith(".outlook.com", StringComparison.Ordinal))
        {
            return new CalendarWeb("Outlook", host.Contains("office", StringComparison.Ordinal) ? "https://outlook.office.com/calendar" : "https://outlook.live.com/calendar", null);
        }

        return host.EndsWith("icloud.com", StringComparison.Ordinal)
            ? new CalendarWeb("iCloud Calendar", "https://www.icloud.com/calendar/", null)
            : new CalendarWeb("your calendar", $"{uri.Scheme}://{uri.Host}/", null);
    }

    /// <summary>The calendar opened on a day.</summary>
    public string Day(DateTimeOffset day) => Name switch
    {
        "Google Calendar" => $"{Home}/day/{day.Year}/{day.Month}/{day.Day}{AuthUser("?")}",
        "Outlook" => $"{Home}/view/day/{day:yyyy-MM-dd}",
        _ => Home,
    };

    /// <summary>The calendar's own new-event page with the event filled in, or null where it has none.</summary>
    public string? NewEvent(CalendarEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        static string Q(string text) => Uri.EscapeDataString(text);
        switch (Name)
        {
            case "Google Calendar":
                var dates = e.AllDay
                    ? $"{e.Start:yyyyMMdd}/{e.End:yyyyMMdd}"
                    : $"{e.Start.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}/{e.End.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
                return $"https://calendar.google.com/calendar/render?action=TEMPLATE&text={Q(e.Title)}&dates={dates}&location={Q(e.Location)}&details={Q(e.Notes)}{AuthUser("&")}";
            case "Outlook":
                var format = e.AllDay ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ssK";
                return $"{Home}/deeplink/compose?subject={Q(e.Title)}&startdt={Q(e.Start.ToString(format, CultureInfo.InvariantCulture))}&enddt={Q(e.End.ToString(format, CultureInfo.InvariantCulture))}&location={Q(e.Location)}&body={Q(e.Notes)}{(e.AllDay ? "&allday=true" : string.Empty)}";
            default:
                return null;
        }
    }

    private string AuthUser(string separator) => Account is null ? string.Empty : $"{separator}authuser={Uri.EscapeDataString(Account)}";
}
