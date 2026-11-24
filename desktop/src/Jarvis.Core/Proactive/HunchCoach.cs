using Jarvis.Contracts.Activity;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Proactive;

/// <summary>Settings → Hunches (settings.json until it has a page).</summary>
public sealed class HunchOptions
{
    public const string SectionName = "Hunches";

    /// <summary>Ask hunches unprompted: one at a time, when the notch is free and the user is at the keyboard.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Note the app and window title in front. They go only to the vault's brain on this PC, never to Claude.</summary>
    public bool Observe { get; set; } = true;

    /// <summary>Minutes between unprompted hunches. Answering one brings the next straight away.</summary>
    public int GapMinutes { get; set; } = 20;
}

/// <summary>
/// Jarvis getting to know the user, built into the notch. In the background it notes which app and
/// window are in front (runs of time, sent in batches to the vault's brain, which forms the guesses on
/// this PC). When the user is around and the notch is free, it shows one hunch: "I have a hunch… you
/// do your real work after 10 pm, don't you?" with three answers, "In my words", "Why?" (what it
/// noticed) and Skip. Skip, a click elsewhere or no answer in a few minutes puts the hunch on the
/// stack: not asked again this session, first in the next. Answering brings the next hunch at once.
/// The notch home's Hunches tile asks on demand, the stack included. Ticked every 30 seconds.
/// </summary>
public sealed partial class HunchCoach
{
    private static readonly TimeSpan SampleLength = TimeSpan.FromSeconds(30); // one tick
    private static readonly TimeSpan ActiveWithin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CardTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan NothingToAskBackoff = TimeSpan.FromMinutes(15);
    private const int MaxRunSeconds = 3600;
    private const int MaxBuffered = 500;

    private readonly IVault _vault;
    private readonly IActivityWatch _activity;
    private readonly INotchPresenter _notch;
    private readonly PauseSwitch _pause;
    private readonly IOptionsMonitor<HunchOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HunchCoach> _logger;
    private readonly Lock _gate = new();
    private readonly List<ActivityObservation> _buffer = [];
    private ActivityObservation? _run;
    private DateTimeOffset _lastFlush;
    private DateTimeOffset _lastAsked;
    private DateTimeOffset _quietUntil;
    private Card? _card;

    /// <param name="session">This run of Jarvis: a hunch skipped now comes back in the next one.</param>
    public HunchCoach(IVault vault, IActivityWatch activity, INotchPresenter notch, PauseSwitch pause, IOptionsMonitor<HunchOptions> options, TimeProvider time, ILogger<HunchCoach> logger, string? session = null)
    {
        _vault = vault;
        _activity = activity;
        _notch = notch;
        _pause = pause;
        _options = options;
        _time = time;
        _logger = logger;
        Session = session ?? Guid.NewGuid().ToString("N");
        _lastFlush = _lastAsked = time.GetUtcNow(); // nothing in the first minutes after start-up
    }

    public string Session { get; }

    /// <summary>Hunches on the stack, as of the last ask (for the home's tile).</summary>
    public int Stacked { get; private set; }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_pause.IsPaused || !_vault.IsConnected)
        {
            return;
        }

        var options = _options.CurrentValue;
        var now = _time.GetUtcNow();
        if (options.Observe)
        {
            Sample(now);
        }

        if (now - _lastFlush >= FlushEvery)
        {
            await FlushAsync(now, cancellationToken).ConfigureAwait(false);
        }

        await WatchCardAsync(now).ConfigureAwait(false);
        if (options.Enabled && ShouldAsk(now, options))
        {
            await AskAsync(onDemand: false, lead: null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The home's Hunches tile: the hunch on screen, else the next one (stack first), even past today's cap.</summary>
    public async Task AskNowAsync()
    {
        Card? open;
        lock (_gate)
        {
            open = _card is { Done: false } ? _card : null;
        }

        if (open is not null)
        {
            Show(open, withEvidence: false, lead: null);
            return;
        }

        if (!await AskAsync(onDemand: true, lead: null, CancellationToken.None).ConfigureAwait(false))
        {
            await SayBrieflyAsync("No hunches yet. I'm still noticing; ask me again in a while.").ConfigureAwait(false);
        }
    }

    // --- noticing ------------------------------------------------------------------------------

    private void Sample(DateTimeOffset now)
    {
        var window = _activity.IdleFor() < ActiveWithin ? _activity.Foreground() : null; // away: nothing counts
        lock (_gate)
        {
            if (_run is { } run && window is not null && run.App == window.App && run.Title == window.Title && run.Seconds < MaxRunSeconds)
            {
                _run = run with { Seconds = run.Seconds + (int)SampleLength.TotalSeconds };
                return;
            }

            EndRunLocked();
            _run = window is null ? null : new ActivityObservation(now, window.App, window.Title, (int)SampleLength.TotalSeconds);
        }
    }

    private void EndRunLocked()
    {
        if (_run is { } run)
        {
            _buffer.Add(run);
            if (_buffer.Count > MaxBuffered)
            {
                _buffer.RemoveRange(0, _buffer.Count - MaxBuffered); // the vault's been away a while: keep the newest
            }
        }

        _run = null;
    }

    private async Task FlushAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _lastFlush = now;
        List<ActivityObservation> batch;
        lock (_gate)
        {
            batch = [.. _buffer];
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await _vault.ObserveAsync(batch, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _buffer.RemoveRange(0, Math.Min(batch.Count, _buffer.Count));
            }
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogVaultFailed("send what was noticed", exception.Message); // kept for the next try
        }
    }

    // --- asking --------------------------------------------------------------------------------

    private bool ShouldAsk(DateTimeOffset now, HunchOptions options)
    {
        lock (_gate)
        {
            if (_card is { Done: false })
            {
                return false;
            }
        }

        return _notch.Current is NotchState.IdleState
               && !_activity.InFullscreen
               && _activity.IdleFor() < ActiveWithin
               && now >= _quietUntil
               && now - _lastAsked >= TimeSpan.FromMinutes(Math.Max(1, options.GapMinutes));
    }

    /// <summary>Fetches and shows the next hunch; false when there's none (or the vault can't be reached).</summary>
    private async Task<bool> AskAsync(bool onDemand, string? lead, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        _lastAsked = now;
        HunchBatch? batch;
        try
        {
            batch = await _vault.NextHunchAsync(Session, onDemand, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogVaultFailed("get a hunch", exception.Message);
            _quietUntil = now + NothingToAskBackoff;
            return false;
        }

        Stacked = batch?.Stacked ?? Stacked;
        if (batch?.Hunch is not { } hunch)
        {
            _quietUntil = now + NothingToAskBackoff;
            return false;
        }

        // Unprompted, and the user started something meanwhile: it waits (served once per session, so next session).
        if (!onDemand && lead is null && _notch.Current is not NotchState.IdleState)
        {
            return true;
        }

        Show(new Card(hunch), withEvidence: false, lead);
        return true;
    }

    private void Show(Card card, bool withEvidence, string? lead)
    {
        var hunch = card.Hunch;
        var text = (lead is null ? string.Empty : lead + " ")
                   + (withEvidence ? $"What I noticed: {string.Join("; ", hunch.Evidence)}. " : "I have a hunch… ")
                   + hunch.Question;
        List<NotchAction> actions = [.. hunch.Options.Select((option, choice) => new NotchAction(option, () => _ = AnswerAsync(card, choice, null)))];
        actions.Add(new NotchAction("In my words", () => Type(card)));
        if (!withEvidence && hunch.Evidence.Count > 0)
        {
            actions.Add(new NotchAction("Why?", () => Show(card, withEvidence: true, lead: null)));
        }

        actions.Add(new NotchAction("Skip", () => _ = SkipAsync(card)));
        Present(card, new NotchState.Response(text, actions));
    }

    private void Type(Card card)
        => Present(card, new NotchState.Composing(
            text =>
            {
                if (text.Trim().Length > 0)
                {
                    _ = AnswerAsync(card, null, text.Trim());
                }
                else
                {
                    _ = SkipAsync(card);
                }
            },
            () => _ = SkipAsync(card)));

    private void Present(Card card, NotchState state)
    {
        lock (_gate)
        {
            _card = card;
            card.Shown = state;
            card.At = _time.GetUtcNow();
        }

        _notch.Show(state);
    }

    /// <summary>
    /// The card isn't on screen any more and wasn't answered (a click elsewhere, Esc, something else
    /// took the notch), or it's been up too long: on the stack it goes.
    /// </summary>
    /// <remarks>
    /// ponytail: a button closes the panel a moment before its action runs; a tick landing in that
    /// gap would stack a hunch being answered. The gap is microseconds against a 30 s tick.
    /// </remarks>
    private async Task WatchCardAsync(DateTimeOffset now)
    {
        Card? card;
        lock (_gate)
        {
            card = _card is { Done: false } ? _card : null;
        }

        if (card is null)
        {
            return;
        }

        if (!ReferenceEquals(_notch.Current, card.Shown) || now - card.At > CardTimeout)
        {
            _notch.RestIfShowing(card.Shown);
            await SkipAsync(card).ConfigureAwait(false);
        }
    }

    private bool Finish(Card card)
    {
        lock (_gate)
        {
            if (card.Done)
            {
                return false; // a double tap, or the watcher got there first
            }

            card.Done = true;
            if (ReferenceEquals(_card, card))
            {
                _card = null;
            }

            return true;
        }
    }

    private async Task AnswerAsync(Card card, int? choice, string? text)
    {
        if (!Finish(card))
        {
            return;
        }

        string? savedTo;
        try
        {
            savedTo = await _vault.AnswerHunchAsync(card.Hunch.Id, choice, text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            LogVaultFailed("save an answer", exception.Message);
            await SayBrieflyAsync($"I couldn't save that answer: {exception.Message}").ConfigureAwait(false);
            return;
        }

        // Momentum: while the user is answering, the next hunch follows straight away.
        var noted = $"Noted in {(savedTo is { Length: > 0 } ? savedTo : "your profile")}.";
        if (!await AskAsync(onDemand: false, lead: noted, CancellationToken.None).ConfigureAwait(false))
        {
            await SayBrieflyAsync(noted + " That's all my hunches for now.").ConfigureAwait(false);
        }
    }

    private async Task SkipAsync(Card card)
    {
        if (!Finish(card))
        {
            return;
        }

        try
        {
            await _vault.SkipHunchAsync(card.Hunch.Id, Session, CancellationToken.None).ConfigureAwait(false);
            Stacked++;
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            LogVaultFailed("stack a hunch", exception.Message); // the brain asks it again next session anyway
        }
    }

    private async Task SayBrieflyAsync(string text)
    {
        var said = new NotchState.Response(text);
        _notch.Show(said);
        await Task.Delay(TimeSpan.FromSeconds(4), _time).ConfigureAwait(false);
        _notch.RestIfShowing(said);
    }

    private sealed class Card(Hunch hunch)
    {
        public Hunch Hunch { get; } = hunch;

        public NotchState Shown { get; set; } = NotchState.Idle;

        public DateTimeOffset At { get; set; }

        public bool Done { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hunches: couldn't {What}: {Reason}")]
    private partial void LogVaultFailed(string what, string reason);
}
