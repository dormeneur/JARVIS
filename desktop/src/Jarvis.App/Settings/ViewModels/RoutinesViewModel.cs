using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Core.Agents;

namespace Jarvis.App.Settings.ViewModels;

public sealed partial class RoutinesViewModel : ObservableObject
{
    private const string Examples = "daily 08:00, weekdays 09:30, monday 10:00, every 2 hours";

    private readonly IRoutineStore _store;
    private readonly RoutineScheduler _scheduler;
    private readonly TimeProvider _time;

    public RoutinesViewModel(IRoutineStore store, RoutineScheduler scheduler, TimeProvider time)
    {
        _store = store;
        _scheduler = scheduler;
        _time = time;
        Refresh();
    }

    public ObservableCollection<RoutineRow> Routines { get; } = [];

    public bool IsEmpty => Routines.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewBrief { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string NewSchedule { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScheduleHint { get; set; } = $"For example: {Examples}.";

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (!RoutineSchedule.IsValid(NewSchedule))
        {
            ScheduleHint = $"Jarvis doesn't understand that schedule. Try: {Examples} (15 minutes or more).";
            return;
        }

        _store.Add(new Routine(Guid.NewGuid().ToString("N"), NewSchedule.Trim(), NewBrief.Trim(), _time.GetUtcNow(), null));
        NewBrief = string.Empty;
        NewSchedule = string.Empty;
        ScheduleHint = "Added. It first runs at its next scheduled time.";
        Refresh();
    }

    [RelayCommand]
    private void Remove(string id)
    {
        _store.Remove(id);
        Refresh();
    }

    [RelayCommand]
    private void RunNow(string id)
    {
        if (_store.All.FirstOrDefault(routine => routine.Id == id) is { } routine)
        {
            _scheduler.Run(routine, _time.GetUtcNow());
            Refresh();
        }
    }

    private bool CanAdd() => NewBrief.Trim().Length > 0 && NewSchedule.Trim().Length > 0;

    private void Refresh()
    {
        Routines.Clear();
        foreach (var routine in _store.All)
        {
            DateTimeOffset? next = RoutineSchedule.IsValid(routine.Schedule)
                ? RoutineSchedule.NextAfter(routine.Schedule, routine.LastRun ?? routine.Created, _time.LocalTimeZone)
                : null;
            Routines.Add(new RoutineRow(routine.Id, RoutineSchedule.Describe(routine.Schedule), routine.Brief, Status(routine.LastRun, next)));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private string Status(DateTimeOffset? lastRun, DateTimeOffset? next)
    {
        var last = lastRun is { } ran ? $"Last ran {When(ran)}" : "Hasn't run yet";
        return next is { } upcoming ? $"{last} · next {When(upcoming)}" : last;
    }

    private string When(DateTimeOffset at)
        => TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);
}

public sealed record RoutineRow(string Id, string Schedule, string Brief, string Status);
