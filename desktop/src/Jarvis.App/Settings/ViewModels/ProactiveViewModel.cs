using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Core.Proactive;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class ProactiveViewModel : ObservableObject
{
    private static readonly int[] HeadsUpMinutes = [0, 1, 2, 5, 10, 15];
    private static readonly int[] CadenceMinutes = [30, 60, 120, 240];

    private readonly SettingsStore _store;
    private readonly CalendarFeed _calendar;

    /// <summary>False while the constructor loads current values, so loading isn't mistaken for a change.</summary>
    private readonly bool _ready;

    public ProactiveViewModel(SettingsStore store, IOptionsMonitor<ProactiveOptions> options, HttpClient http, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _calendar = new CalendarFeed(http, time); // its own copy: Check always fetches afresh
        var current = options.CurrentValue;
        Speak = current.Speak;
        MorningBrief = current.MorningBrief;
        MorningFrom = TimeSpan.TryParseExact(current.MorningFrom, @"h\:mm", CultureInfo.InvariantCulture, out var from) ? from : TimeSpan.FromHours(6);
        CalendarAddress = current.CalendarAddress;
        HeadsUpIndex = Math.Max(0, Array.IndexOf(HeadsUpMinutes, current.MeetingHeadsUpMinutes));
        Suggestions = current.Suggestions;
        CadenceIndex = Math.Max(0, Array.IndexOf(CadenceMinutes, current.SuggestEveryMinutes));
        _ready = true;
    }

    public IReadOnlyList<string> HeadsUpChoices { get; } = ["Off", "1 minute before", "2 minutes before", "5 minutes before", "10 minutes before", "15 minutes before"];

    public IReadOnlyList<string> CadenceChoices { get; } = ["Every 30 minutes", "Every hour", "Every 2 hours", "Every 4 hours"];

    [ObservableProperty]
    public partial bool Speak { get; set; }

    [ObservableProperty]
    public partial bool MorningBrief { get; set; }

    [ObservableProperty]
    public partial TimeSpan MorningFrom { get; set; }

    [ObservableProperty]
    public partial string CalendarAddress { get; set; }

    [ObservableProperty]
    public partial string CalendarStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int HeadsUpIndex { get; set; }

    [ObservableProperty]
    public partial bool Suggestions { get; set; }

    [ObservableProperty]
    public partial int CadenceIndex { get; set; }

    [RelayCommand]
    private async Task CheckCalendarAsync()
    {
        if (string.IsNullOrWhiteSpace(CalendarAddress))
        {
            CalendarStatus = "Paste an address first.";
            return;
        }

        CalendarStatus = "Checking…";
        try
        {
            var meetings = await _calendar.UpcomingAsync(CalendarAddress, TimeZoneInfo.Local, CancellationToken.None);
            var next = meetings.FirstOrDefault(meeting => meeting.Start > DateTimeOffset.Now);
            CalendarStatus = next is null
                ? "It works. No meetings in the next day."
                : $"It works. {meetings.Count} meeting{(meetings.Count == 1 ? string.Empty : "s")} in the next day; next: {next.Title} at {next.Start.ToLocalTime():t}.";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            CalendarStatus = $"Couldn't read it: {exception.Message}";
        }
    }

    partial void OnSpeakChanged(bool value) => Save(nameof(ProactiveOptions.Speak), value);

    partial void OnMorningBriefChanged(bool value) => Save(nameof(ProactiveOptions.MorningBrief), value);

    partial void OnSuggestionsChanged(bool value) => Save(nameof(ProactiveOptions.Suggestions), value);

    partial void OnMorningFromChanged(TimeSpan value)
    {
        if (_ready)
        {
            _store.Set(ProactiveOptions.SectionName, nameof(ProactiveOptions.MorningFrom), value.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        }
    }

    partial void OnCalendarAddressChanged(string value)
    {
        if (_ready)
        {
            _store.Set(ProactiveOptions.SectionName, nameof(ProactiveOptions.CalendarAddress), value.Trim());
            CalendarStatus = string.Empty;
        }
    }

    partial void OnHeadsUpIndexChanged(int value)
    {
        if (_ready && value >= 0)
        {
            _store.Set(ProactiveOptions.SectionName, nameof(ProactiveOptions.MeetingHeadsUpMinutes), HeadsUpMinutes[value]);
        }
    }

    partial void OnCadenceIndexChanged(int value)
    {
        if (_ready && value >= 0)
        {
            _store.Set(ProactiveOptions.SectionName, nameof(ProactiveOptions.SuggestEveryMinutes), CadenceMinutes[value]);
        }
    }

    private void Save(string key, bool value)
    {
        if (_ready)
        {
            _store.Set(ProactiveOptions.SectionName, key, value);
        }
    }
}
