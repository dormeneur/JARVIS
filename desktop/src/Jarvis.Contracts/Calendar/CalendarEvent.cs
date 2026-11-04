namespace Jarvis.Contracts.Calendar;

/// <summary>An event the user asked to add, shown to them to check, edit or cancel before it's added.</summary>
/// <param name="End">For an all-day event, the day after the last day (as iCalendar has it).</param>
public sealed record CalendarEvent(string Title, DateTimeOffset Start, DateTimeOffset End, bool AllDay, string Location, string Notes);
