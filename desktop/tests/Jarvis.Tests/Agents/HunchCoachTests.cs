using System.Runtime.CompilerServices;
using Jarvis.Contracts.Activity;
using Jarvis.Contracts.Browser;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Vault;
using Jarvis.Core;
using Jarvis.Core.Proactive;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Tests.Agents;

public sealed class HunchCoachTests
{
    private static readonly Hunch Night = new("h1", "You do your real work after 10 pm, don't you?", ["Spot on", "Only on deadlines", "Morning person"], ["VS Code at 23:00 most nights"]);
    private static readonly Hunch Videos = new("h2", "You learn new tools from videos, not docs?", ["Always", "Depends", "I read docs"], ["9 tutorials, 1 docs page"]);

    [Fact]
    public async Task Asks_WhenTheNotchIsFreeAndTheUserIsAround()
    {
        var (coach, f) = Make(Night);
        f.Clock.Now += TimeSpan.FromMinutes(21);
        await coach.TickAsync(CancellationToken.None);

        var card = Assert.IsType<NotchState.Response>(f.Notch.Current);
        Assert.StartsWith("You do your real work", card.Text, StringComparison.Ordinal);
        Assert.Equal("✦  I have a hunch", card.Card!.Label);
        Assert.Equal(["Spot on", "Only on deadlines", "Morning person", "In my words", "Why?", "Skip"], card.Actions!.Select(a => a.Label));
        Assert.Equal([false], f.Vault.Asks); // unprompted
    }

    [Theory]
    [InlineData("listening")]
    [InlineData("away")]
    [InlineData("fullscreen")]
    [InlineData("too soon")]
    public async Task NeverAsks_OverSomethingElse_OrWhenAwayOrBusy(string why)
    {
        var (coach, f) = Make(Night);
        f.Clock.Now += TimeSpan.FromMinutes(why == "too soon" ? 5 : 21);
        if (why == "listening")
        {
            f.Notch.Current = new NotchState.Listening();
        }

        f.Activity.Idle = why == "away" ? TimeSpan.FromMinutes(5) : TimeSpan.Zero;
        f.Activity.Fullscreen = why == "fullscreen";
        await coach.TickAsync(CancellationToken.None);
        Assert.Empty(f.Vault.Asks);
    }

    [Fact]
    public async Task ClickingAway_PutsItOnTheStack()
    {
        var (coach, f) = Make(Night);
        f.Clock.Now += TimeSpan.FromMinutes(21);
        await coach.TickAsync(CancellationToken.None);
        f.Notch.Current = NotchState.Idle; // the user clicked elsewhere: the notch closed it

        f.Clock.Now += TimeSpan.FromSeconds(30);
        await coach.TickAsync(CancellationToken.None);
        Assert.Equal([("h1", coach.Session)], f.Vault.Skipped);
        Assert.Equal(1, coach.Stacked);
    }

    [Fact]
    public async Task AnUnansweredCard_GoesOnTheStackAfterAFewMinutes()
    {
        var (coach, f) = Make(Night);
        f.Clock.Now += TimeSpan.FromMinutes(21);
        await coach.TickAsync(CancellationToken.None);

        f.Clock.Now += TimeSpan.FromMinutes(4);
        await coach.TickAsync(CancellationToken.None);
        Assert.Single(f.Vault.Skipped);
        Assert.IsType<NotchState.IdleState>(f.Notch.Current);
    }

    [Fact]
    public async Task Answering_SavesIt_AndBringsTheNextHunchAtOnce()
    {
        var (coach, f) = Make(Night, Videos);
        f.Clock.Now += TimeSpan.FromMinutes(21);
        await coach.TickAsync(CancellationToken.None);

        Press(f.Notch, "Only on deadlines");
        await WaitUntil(() => f.Notch.Current is NotchState.Response r && r.Text.Contains("videos", StringComparison.Ordinal));
        Assert.Equal([("h1", (int?)1, (string?)null)], f.Vault.Answers);
        Assert.Equal("✓  Noted in Profile/traits.md.  ·  next hunch", ((NotchState.Response)f.Notch.Current).Card!.Label);
    }

    [Fact]
    public async Task Why_ShowsWhatItNoticed_AndCanStillBeAnswered()
    {
        var (coach, f) = Make(Night);
        f.Clock.Now += TimeSpan.FromMinutes(21);
        await coach.TickAsync(CancellationToken.None);

        Press(f.Notch, "Why?");
        var why = Assert.IsType<NotchState.Response>(f.Notch.Current);
        Assert.Equal("What I noticed: VS Code at 23:00 most nights", why.Card!.Footnote);
        Assert.DoesNotContain(why.Actions!, a => a.Label == "Why?");

        f.Clock.Now += TimeSpan.FromSeconds(30);
        await coach.TickAsync(CancellationToken.None); // still showing (the new card): not stacked
        Assert.Empty(f.Vault.Skipped);
        Press(f.Notch, "Spot on");
        await WaitUntil(() => f.Vault.Answers.Count == 1);
    }

    [Fact]
    public async Task InMyWords_SendsWhatTheUserTyped()
    {
        var (coach, f) = Make(Night);
        f.Clock.Now += TimeSpan.FromMinutes(21);
        await coach.TickAsync(CancellationToken.None);

        Press(f.Notch, "In my words");
        Assert.IsType<NotchState.Composing>(f.Notch.Current).Submit("  only when a deadline is close ");
        await WaitUntil(() => f.Vault.Answers.Count == 1);
        Assert.Equal(("h1", (int?)null, (string?)"only when a deadline is close"), f.Vault.Answers[0]);
    }

    [Fact]
    public async Task OnDemand_AsksEvenWhenTheBrainWouldWait_AndSaysSoWhenThereIsNone()
    {
        var (coach, f) = Make();
        await coach.AskNowAsync();
        Assert.Equal([true], f.Vault.Asks);
        Assert.Contains(f.Notch.Shown, s => s is NotchState.Response r && r.Text.StartsWith("No hunches yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Noticing_SendsRunsOfTimePerWindow_AndNothingWhileAway()
    {
        var (coach, f) = Make();
        f.Activity.Window = new ForegroundWindow("Code", "agent.py - JARVIS");
        for (var i = 0; i < 3; i++)
        {
            await coach.TickAsync(CancellationToken.None);
        }

        f.Activity.Window = new ForegroundWindow("chrome", "Rust tutorial - YouTube");
        await coach.TickAsync(CancellationToken.None);
        f.Activity.Idle = TimeSpan.FromMinutes(10); // away: the YouTube run ends, nothing new starts
        f.Clock.Now += TimeSpan.FromMinutes(11);
        await coach.TickAsync(CancellationToken.None);

        Assert.Equal([("Code", "agent.py - JARVIS", 90), ("chrome", "Rust tutorial - YouTube", 30)], f.Vault.Observed.Select(o => (o.App, o.Title, o.Seconds)));
    }

    [Fact]
    public async Task Paused_DoesNothingAtAll()
    {
        var (coach, f) = Make(Night);
        f.Pause.Set(true);
        f.Activity.Window = new ForegroundWindow("Code", "x");
        f.Clock.Now += TimeSpan.FromMinutes(30);
        await coach.TickAsync(CancellationToken.None);
        Assert.Empty(f.Vault.Asks);
        Assert.Empty(f.Vault.Observed);
    }

    private static void Press(Notch notch, string label)
    {
        var action = Assert.IsType<NotchState.Response>(notch.Current).Actions!.Single(a => a.Label == label);
        notch.Current = NotchState.Idle; // a button closes the panel, then runs
        action.Invoke();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static (HunchCoach Coach, Fakes F) Make(params Hunch[] hunches)
    {
        var f = new Fakes(hunches);
        var coach = new HunchCoach(f.Vault, f.Activity, f.Notch, f.Pause, new Monitor(new HunchOptions()), f.Clock, NullLogger<HunchCoach>.Instance, "session-1");
        return (coach, f);
    }

    private sealed class Fakes(Hunch[] hunches)
    {
        public Clock Clock { get; } = new() { Now = new DateTimeOffset(2026, 11, 23, 20, 0, 0, TimeSpan.Zero) };

        public HunchVault Vault { get; } = new(hunches);

        public Activity Activity { get; } = new();

        public Notch Notch { get; } = new();

        public PauseSwitch Pause { get; } = new();
    }

    private sealed class Monitor(HunchOptions value) : IOptionsMonitor<HunchOptions>
    {
        public HunchOptions CurrentValue => value;

        public HunchOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<HunchOptions, string?> listener) => null;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Activity : IActivityWatch
    {
        public ForegroundWindow? Window { get; set; }

        public TimeSpan Idle { get; set; }

        public bool Fullscreen { get; set; }

        public bool InFullscreen => Fullscreen;

        public ForegroundWindow? Foreground() => Window;

        public TimeSpan IdleFor() => Idle;
    }

    private sealed class Notch : INotchPresenter
    {
        private readonly Lock _gate = new();
        private NotchState _current = NotchState.Idle;

        public List<NotchState> Shown { get; } = [];

        public NotchState Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }

            set
            {
                lock (_gate)
                {
                    _current = value;
                }
            }
        }

        public void Show(NotchState state)
        {
            lock (_gate)
            {
                _current = state;
                Shown.Add(state);
            }
        }
    }

    private sealed class HunchVault(Hunch[] hunches) : IVault
    {
        private readonly Queue<Hunch> _queue = new(hunches);

        public bool IsConnected => true;

        public List<bool> Asks { get; } = [];

        public List<(string Id, int? Choice, string? Text)> Answers { get; } = [];

        public List<(string Id, string Session)> Skipped { get; } = [];

        public List<ActivityObservation> Observed { get; } = [];

        public Task ObserveAsync(IReadOnlyList<ActivityObservation> samples, CancellationToken cancellationToken)
        {
            Observed.AddRange(samples);
            return Task.CompletedTask;
        }

        public Task<HunchBatch?> NextHunchAsync(string session, bool onDemand, CancellationToken cancellationToken)
        {
            Asks.Add(onDemand);
            return Task.FromResult<HunchBatch?>(new HunchBatch(_queue.TryDequeue(out var next) ? next : null, Skipped.Count, 5));
        }

        public Task<string?> AnswerHunchAsync(string id, int? choice, string? text, CancellationToken cancellationToken)
        {
            Answers.Add((id, choice, text));
            return Task.FromResult<string?>(choice == 2 ? "Profile/not-me.md" : "Profile/traits.md");
        }

        public Task SkipHunchAsync(string id, string session, CancellationToken cancellationToken)
        {
            Skipped.Add((id, session));
            return Task.CompletedTask;
        }

        public Task ConnectAsync(string inviteCode, CancellationToken cancellationToken) => Task.CompletedTask;

        public void Disconnect()
        {
        }

        public async IAsyncEnumerable<FormAnswer> AnswerFormAsync(string title, IReadOnlyList<FormQuestion> questions, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<string> ComposeAnswerAsync(string question, string title, string research, IReadOnlyList<string> attachments, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<VaultEntry>> ListAsync(string folder, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VaultEntry>>([]);

        public Task UploadAsync(string path, string localFile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public async IAsyncEnumerable<string> AskAsync(string question, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<string?> ReadFileAsync(string path, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
