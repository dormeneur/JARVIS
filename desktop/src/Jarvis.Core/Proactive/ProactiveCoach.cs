using System.Globalization;
using System.Text;
using Jarvis.Contracts.Activity;
using Jarvis.Contracts.Brain;
using Jarvis.Contracts.Notch;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Proactive;

/// <summary>Settings → Proactive.</summary>
public sealed class ProactiveOptions
{
    public const string SectionName = "Proactive";

    /// <summary>Say good morning, with the day's meetings, the first time the user is around each morning.</summary>
    public bool MorningBrief { get; set; } = true;

    /// <summary>The morning starts at this local time (HH:mm); the brief is offered until noon.</summary>
    public string MorningFrom { get; set; } = "06:00";

    /// <summary>A calendar's private iCal (.ics) address, e.g. Google Calendar's "secret address". Empty = none.</summary>
    public string CalendarAddress { get; set; } = string.Empty;

    /// <summary>How long before a meeting to give a heads-up; 0 = never.</summary>
    public int MeetingHeadsUpMinutes { get; set; } = 5;

    /// <summary>
    /// Every so often, send the names of the user's recent windows to Claude so it can offer a
    /// background job that would help. Off unless the user turns it on.
    /// </summary>
    public bool Suggestions { get; set; }

    public int SuggestEveryMinutes { get; set; } = 60;

    /// <summary>Read the brief, heads-ups and offers aloud, not just show them.</summary>
    public bool Speak { get; set; } = true;
}

/// <param name="NewBrain">A fresh, tool-less brain for one suggestion; disposed afterwards.</param>
/// <param name="Say">Shows a caption with buttons, speaking the second argument if not null.</param>
public sealed record ProactiveServices(
    IActivityWatch Activity,
    CalendarFeed Calendar,
    IAgentJobs Jobs,
    INotchPresenter Notch,
    Func<IAssistantBrain> NewBrain,
    ScreenPrivacy Privacy,
    PauseSwitch Pause,
    Func<string, string?, IReadOnlyList<NotchAction>, Task> Say);

/// <summary>
/// Jarvis speaking first: a morning brief, a heads-up before meetings, and (only if the user opts
/// in) an occasional offer of a background job based on what they've been doing. Ticked every
/// 30 seconds. Never interrupts: it waits for the notch to be at rest, skips fullscreen apps and
/// pauses, and only greets or suggests while the user is at the keyboard.
/// </summary>
public sealed partial class ProactiveCoach
{
    private static readonly TimeSpan ActiveWithin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActivityKept = TimeSpan.FromHours(1);
    private const int MinimumSamples = 10; // five minutes of activity before suggesting anything
    private const int OffersRemembered = 10;
    private static readonly TimeSpan SuggestionTimeout = TimeSpan.FromSeconds(90);

    private readonly ProactiveServices _services;
    private readonly IOptionsMonitor<ProactiveOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ProactiveCoach> _logger;
    private readonly string _morningFile;
    private readonly Queue<(DateTimeOffset At, ForegroundWindow Window)> _activity = new();
    private readonly HashSet<(string, DateTimeOffset)> _warned = [];
    private readonly List<string> _offers = [];
    private DateTimeOffset _lastSuggestion;

    /// <param name="morningFile">Remembers the day of the last brief, so a restart doesn't repeat it.</param>
    public ProactiveCoach(ProactiveServices services, IOptionsMonitor<ProactiveOptions> options, TimeProvider time, ILogger<ProactiveCoach> logger, string morningFile)
    {
        _services = services;
        _options = options;
        _time = time;
        _logger = logger;
        _morningFile = morningFile;
        _lastSuggestion = time.GetUtcNow(); // not the moment Jarvis starts
    }

    public const string SuggestInstructions = """
        You help a Windows user by offering, at most now and then, one background job that would clearly
        save them time. You see only the names of the apps and windows they've had in front recently.
        A background helper can search and read the web, write documents and files, and use services
        the user has connected; it cannot use their apps or PC.
        Reply with exactly one line, either
        SUGGEST: <the offer they'll see, a short question under 12 words> | <the brief for the helper: exactly what to produce>
        or, if nothing would clearly and specifically help, exactly
        NONE
        Most of the time the answer is NONE. Never offer anything about passwords, money, health or
        what's in private messages, and never repeat or rephrase a recent offer.
        """;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (_services.Pause.IsPaused)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (options.Suggestions)
        {
            Sample(now);
        }

        if (_services.Notch.Current is not NotchState.IdleState || _services.Activity.InFullscreen)
        {
            return; // never talk over the user or their game
        }

        var meetings = await MeetingsAsync(options, cancellationToken).ConfigureAwait(false);
        if (HeadsUp(meetings, now, options) is not null)
        {
            return;
        }

        var local = TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone);
        var active = _services.Activity.IdleFor() < ActiveWithin;
        if (options.MorningBrief && active && IsMorningDue(local, options))
        {
            File.WriteAllText(_morningFile, local.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var brief = MorningBrief(local, meetings, options.CalendarAddress.Length > 0);
            _ = _services.Say(brief, options.Speak ? brief : null, [Dismiss()]);
            return;
        }

        if (options.Suggestions && !_services.Privacy.PrivateMode && active && now - _lastSuggestion >= TimeSpan.FromMinutes(Math.Max(15, options.SuggestEveryMinutes)) && _activity.Count >= MinimumSamples)
        {
            _lastSuggestion = now;
            await SuggestAsync(options, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>"Good morning", and the day's meetings when there's a calendar.</summary>
    internal static string MorningBrief(DateTimeOffset local, IReadOnlyList<Meeting> meetings, bool hasCalendar)
    {
        var today = meetings.Where(meeting => meeting.Start >= local && meeting.Start.ToOffset(local.Offset).Date == local.Date).ToList();
        string At(Meeting meeting) => meeting.Start.ToOffset(local.Offset).ToString("t", CultureInfo.CurrentCulture);
        var day = (today.Count, hasCalendar) switch
        {
            (0, false) => string.Empty,
            (0, true) => " Your calendar is clear today.",
            (1, _) => $" You have one meeting today: {today[0].Title} at {At(today[0])}.",
            _ => $" You have {today.Count} meetings today; the first is {today[0].Title} at {At(today[0])}.",
        };
        return "Good morning." + day;
    }

    private Meeting? HeadsUp(IReadOnlyList<Meeting> meetings, DateTimeOffset now, ProactiveOptions options)
    {
        if (options.MeetingHeadsUpMinutes <= 0)
        {
            return null;
        }

        var soon = meetings.FirstOrDefault(meeting => meeting.Start > now
                                                      && meeting.Start - now <= TimeSpan.FromMinutes(options.MeetingHeadsUpMinutes)
                                                      && !_warned.Contains((meeting.Title, meeting.Start)));
        if (soon is null)
        {
            return null;
        }

        _warned.Add((soon.Title, soon.Start));
        var minutes = (int)Math.Ceiling((soon.Start - now).TotalMinutes);
        var text = $"{soon.Title} starts in {minutes} minute{(minutes == 1 ? string.Empty : "s")}.";
        List<NotchAction> actions = soon.Link is { } link ? [new NotchAction("Join", () => Open(link)), Dismiss()] : [Dismiss()];
        _ = _services.Say(text, options.Speak ? text : null, actions);
        return soon;
    }

    private bool IsMorningDue(DateTimeOffset local, ProactiveOptions options)
    {
        var from = TimeSpan.TryParseExact(options.MorningFrom, @"h\:mm", CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.FromHours(6);
        if (local.TimeOfDay < from || local.Hour >= 12)
        {
            return false;
        }

        var last = File.Exists(_morningFile) ? File.ReadAllText(_morningFile).Trim() : string.Empty;
        return last != local.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private async Task<IReadOnlyList<Meeting>> MeetingsAsync(ProactiveOptions options, CancellationToken cancellationToken)
    {
        if (options.CalendarAddress.Length == 0)
        {
            return [];
        }

        try
        {
            return await _services.Calendar.UpcomingAsync(options.CalendarAddress, _time.LocalTimeZone, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogCalendarFailed(exception.Message);
            return [];
        }
    }

    private void Sample(DateTimeOffset now)
    {
        if (_services.Activity.Foreground() is { } window)
        {
            _activity.Enqueue((now, window));
        }

        while (_activity.TryPeek(out var oldest) && now - oldest.At > ActivityKept)
        {
            _activity.Dequeue();
        }
    }

    /// <summary>What the user has had in front, most-used first, with rough minutes (one sample per tick).</summary>
    internal static string DescribeActivity(IEnumerable<ForegroundWindow> samples)
    {
        var text = new StringBuilder("Windows in front over the last hour (about 30 seconds per sample):\n");
        foreach (var group in samples.GroupBy(window => window).OrderByDescending(group => group.Count()).Take(12))
        {
            var title = group.Key.Title.Length > 90 ? group.Key.Title[..90] + "…" : group.Key.Title;
            text.Append(CultureInfo.InvariantCulture, $"- {group.Key.App}: {title} ({group.Count()} samples)\n");
        }

        return text.ToString();
    }

    /// <summary>Reads "SUGGEST: offer | brief"; null for NONE or anything else.</summary>
    internal static (string Offer, string Brief)? ParseSuggestion(string reply)
    {
        var line = reply.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.StartsWith("SUGGEST:", StringComparison.OrdinalIgnoreCase));
        var parts = line?["SUGGEST:".Length..].Split('|', 2, StringSplitOptions.TrimEntries);
        return parts is [{ Length: > 0 } offer, { Length: > 0 } brief] ? (offer, brief) : null;
    }

    private async Task SuggestAsync(ProactiveOptions options, CancellationToken cancellationToken)
    {
        var request = DescribeActivity(_activity.Select(sample => sample.Window));
        if (_offers.Count > 0)
        {
            request += "Recent offers (don't repeat): " + string.Join("; ", _offers) + "\n";
        }

        _services.Privacy.Record(0, "Sent the names of your recent windows to suggest something");
        var brain = _services.NewBrain();
        var reply = new StringBuilder();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SuggestionTimeout);
            await foreach (var brainEvent in brain.RunTurnAsync(new BrainTurn(SuggestInstructions, request, []), timeout.Token).ConfigureAwait(false))
            {
                if (brainEvent is BrainEvent.TextDelta delta)
                {
                    reply.Append(delta.Text);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogSuggestionFailed(exception.Message);
            return;
        }
        finally
        {
            if (brain is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (ParseSuggestion(reply.ToString()) is not var (offer, brief) || _services.Notch.Current is not NotchState.IdleState)
        {
            return; // nothing worth saying, or the user started something meanwhile
        }

        _offers.Add(offer);
        if (_offers.Count > OffersRemembered)
        {
            _offers.RemoveAt(0);
        }

        _ = _services.Say(offer, options.Speak ? offer : null, [new NotchAction("Do it", () => _services.Jobs.Start(brief)), new NotchAction("Not now", () => _services.Notch.Show(NotchState.Idle))]);
    }

    private NotchAction Dismiss() => new("Dismiss", () => _services.Notch.Show(NotchState.Idle));

    private static void Open(string link)
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the calendar: {Reason}")]
    private partial void LogCalendarFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Suggestion failed: {Reason}")]
    private partial void LogSuggestionFailed(string reason);
}
