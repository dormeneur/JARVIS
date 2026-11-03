using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Brain;
using Jarvis.Contracts.Capture;
using Jarvis.Contracts.Control;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Overlay;
using Jarvis.Contracts.Speech;
using Jarvis.Core.Agents;
using Jarvis.Core.Cursor;
using Jarvis.Core.Drawing;
using Jarvis.Core.Vault;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Companion;

/// <summary>
/// One voice turn, end to end: listen while the key is held, transcribe, look at the screens, ask
/// the brain, then speak each sentence as soon as it is written and fly the cursor where it points.
/// Thread-safe: the key hook calls in from its own thread. A new press interrupts the current turn.
/// </summary>
public sealed partial class CompanionSession : IAsyncDisposable
{
    /// <summary>Keep recording briefly after release so the last word isn't clipped.</summary>
    private static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(150);
    private const int MaxDriveSteps = 8;
    private static readonly TimeSpan DriveDecisionTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DriveStepPause = TimeSpan.FromMilliseconds(500);
    private const int MaxDriveRounds = 10;
    private static readonly TimeSpan DriveSettle = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan DriveShowTarget = TimeSpan.FromMilliseconds(700);

    private readonly CompanionServices _services;
    private readonly IOptionsMonitor<CompanionOptions> _options;
    private readonly IOptionsMonitor<CursorOptions> _cursorOptions;
    private readonly TimeProvider _time;
    private readonly ILogger<CompanionSession> _logger;
    private readonly Lock _gate = new();
    private Recording? _recording;
    private ReplyItem.FillForm? _formWanted; // the last cloud reply asked for the form to be filled here
    private Jarvis.Contracts.Browser.BrowserPage? _page; // the browser page the brain last saw (its element and tab ids)
    private string _copied = string.Empty; // what a copy step last copied (the extension keeps it too)
    private CancellationTokenSource? _turn;

    public CompanionSession(
        CompanionServices services,
        IOptionsMonitor<CompanionOptions> options,
        IOptionsMonitor<CursorOptions> cursorOptions,
        TimeProvider time,
        ILogger<CompanionSession> logger)
    {
        _services = services;
        _options = options;
        _cursorOptions = cursorOptions;
        _time = time;
        _logger = logger;
        services.Cursor.Clicked += OnCursorClicked;
    }

    /// <summary>Clicking the left-behind cursor talks hands-free: one click to start, another to finish.</summary>
    private void OnCursorClicked()
    {
        bool recording;
        lock (_gate)
        {
            recording = _recording is not null;
        }

        if (recording)
        {
            EndListening();
        }
        else
        {
            BeginListening();
        }
    }

    /// <summary>The push-to-talk key went down.</summary>
    public void BeginListening()
    {
        try
        {
            lock (_gate)
            {
                InterruptLocked();
                _recording = Recording.Start(_services.Microphone, _services.SpeechToText, Request(), _services.Cursor.ShowLevel, _logger);
                _ = EndIfStillRecordingAsync(_recording);
            }
        }
        catch (MicrophoneBlockedException)
        {
            ShowMicrophoneBlocked();
            return;
        }

        _services.Cursor.SetListening(true);

        _services.Notch.Show(new NotchState.Listening());
        LogListening();
    }

    /// <summary>Windows refused the microphone: say why, with a button to the switch.</summary>
    private void ShowMicrophoneBlocked()
    {
        _services.Cursor.SetListening(false);
        _services.Notch.Show(new NotchState.Response(
            "Windows is blocking Jarvis from the microphone. Turn on \"Let desktop apps access your microphone\" in Windows settings, then try again.",
            [new NotchAction("Open Windows settings", OpenMicrophoneSettings), new NotchAction("Dismiss", () => _services.Notch.Show(NotchState.Idle))]));
    }

    private static void OpenMicrophoneSettings()
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MicrophoneBlockedException.SettingsPage) { UseShellExecute = true });
    }

    /// <summary>The longest recording (DESIGN-FACTS.md §4); hands-free and click-to-talk end here by themselves.</summary>
    internal TimeSpan MaxRecording { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The push-to-talk key came up: answer what was said.</summary>
    public void EndListening()
    {
        CancellationToken token;
        Recording? recording;
        lock (_gate)
        {
            recording = _recording;
            _recording = null;
            if (recording is null)
            {
                return;
            }

            _turn = new CancellationTokenSource();
            token = _turn.Token;
        }

        _services.Cursor.SetListening(false);
        _ = RunTurnAsync(recording, token);
    }

    /// <summary>Escape, or another key during the hold: drop everything and go quiet.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            InterruptLocked();
        }

        _services.Cursor.SetThinking(false);
        _services.Notch.Show(NotchState.Idle);
    }

    /// <summary>
    /// Downloads models on first run and starts every engine, so the first turn is quick. The notch
    /// shows the working light meanwhile, since a first run can take minutes.
    /// </summary>
    public async Task PrewarmAsync(CancellationToken cancellationToken)
    {
        _services.Notch.Show(new NotchState.Working());
        try
        {
            await Task.WhenAll(
                _services.Brain.PrewarmAsync(CompanionPrompt.Instructions, cancellationToken),
                _services.SpeechToText.PrewarmAsync(cancellationToken),
                _services.TextToSpeech.PrewarmAsync(cancellationToken)).ConfigureAwait(false);
            LogReady();
        }
        finally
        {
            _services.Notch.Show(NotchState.Idle);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Recording? recording;
        lock (_gate)
        {
            recording = _recording;
            _recording = null;
            _turn?.Cancel();
            _turn?.Dispose();
            _turn = null;
        }

        if (recording is not null)
        {
            await recording.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Does a task on the user's screen: the steps planned so far, then (while the brain says [MORE])
    /// a fresh look and the next steps, up to <see cref="MaxDriveRounds"/> rounds. The control border
    /// glows the whole time. With "ask every step" each step waits for Do it; with "ask once" one
    /// Take control prompt starts it and the user takes back by moving the mouse or pressing a key.
    /// Stop, Escape or a new question ends it; nothing is ever done unapproved.
    /// </summary>
    private async Task DriveTaskAsync(string question, List<ReplyItem.Act> steps, bool more, IReadOnlyList<CapturedScreen> screens, CancellationToken cancellationToken)
    {
        var askEachStep = _services.Privacy.AskEveryStep;
        if (!askEachStep && !await ApproveAsync(
                "Let Jarvis take control to do this? Move the mouse or press any key to take back.", "Take control", "No", cancellationToken).ConfigureAwait(false))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _services.Notch.Show(new NotchState.Response("Okay, I won't."));
            }

            return;
        }

        using var control = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tookBack = 0;
        using var watch = askEachStep ? null : _services.Control.WatchForUser(() =>
        {
            Interlocked.Exchange(ref tookBack, 1);
            control.Cancel();
        });
        _services.Cursor.ShowControlBorder(true);
        var outcome = DriveOutcome.Done;
        try
        {
            for (var round = 0; ; round++)
            {
                outcome = await RunStepsAsync(steps, screens, askEachStep, control.Token).ConfigureAwait(false);
                if (outcome != DriveOutcome.Done || !more || round + 1 >= MaxDriveRounds)
                {
                    break;
                }

                // Look again once the screen has caught up, and ask for what's next.
                await Task.Delay(DriveSettle, _time, control.Token).ConfigureAwait(false);
                screens = await _services.ScreenCapture.CaptureAsync(control.Token).ConfigureAwait(false);
                _services.Privacy.Record(screens.Count, "Jarvis looked again while doing a task for you");
                _services.Notch.Show(new NotchState.Thinking(_services.Privacy.ShowWhenLooking ? "Looking at your screen" : null));
                var next = CompanionPrompt.DriveOn + CompanionPrompt.ContinueTask(question, round + 1);
                if (_services.Browser.IsConnected && _services.Browser.IsBrowserInFront
                    && await _services.Browser.ReadPageAsync(control.Token).ConfigureAwait(false) is { } page)
                {
                    // The page changed (or another tab is in front): fresh ids for the next steps.
                    _services.Privacy.Record(0, "Read the page open in your browser");
                    next = CompanionPrompt.DescribePage(page) + next;
                    _page = page;
                }

                (_, steps, more, _) = await AnswerAsync(_services.Brain, CompanionPrompt.Instructions, next, screens, _time.GetTimestamp(), typed: false, control.Token).ConfigureAwait(false);
                if (steps.Count == 0)
                {
                    break; // the brain says it's done (or can't go on); it already said why
                }
            }
        }
        catch (OperationCanceledException) when (control.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            outcome = DriveOutcome.Stopped; // the user took back control
        }
        finally
        {
            _services.Cursor.ShowControlBorder(false);
            _services.Cursor.ReturnToMouse();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _services.Notch.Show(new NotchState.Response(Volatile.Read(ref tookBack) == 1
            ? "You took back control. I stopped there."
            : outcome switch
            {
                DriveOutcome.Done => "Done.",
                DriveOutcome.Declined => "Stopped. I didn't do the rest.",
                DriveOutcome.Failed => "I couldn't do one of the steps, so I stopped.",
                _ => "Stopped.",
            }));
    }

    /// <summary>One round's steps, in order; asks before each when <paramref name="askEachStep"/>.</summary>
    private async Task<DriveOutcome> RunStepsAsync(List<ReplyItem.Act> steps, IReadOnlyList<CapturedScreen> screens, bool askEachStep, CancellationToken cancellationToken)
    {
        var planned = steps.Take(MaxDriveSteps).ToList();
        for (var i = 0; i < planned.Count; i++)
        {
            var step = planned[i];
            var browser = IsBrowserStep(step.Kind);
            var pointer = !browser && step.Kind is not (ComputerActionKind.Type or ComputerActionKind.Key or ComputerActionKind.Open);
            var (x, y) = (0, 0);
            if (pointer)
            {
                if ((screens.ElementAtOrDefault(step.Screen - 1) ?? screens.FirstOrDefault()) is not { } screen)
                {
                    return DriveOutcome.Failed;
                }

                (x, y) = screen.ToDesktop(step.X, step.Y);
                _services.Cursor.PointAt(x, y, string.IsNullOrEmpty(step.Label) ? "here" : step.Label);
            }

            if (askEachStep)
            {
                var prefix = planned.Count > 1 ? $"Step {i + 1} of {planned.Count}: " : string.Empty;
                if (!await ApproveAsync($"{prefix}{DriveDescription.Of(step, NameOnPage)}?", "Do it", "Stop", cancellationToken).ConfigureAwait(false))
                {
                    return DriveOutcome.Declined;
                }
            }
            else
            {
                _services.Notch.Show(new NotchState.Thinking(DriveDescription.Of(step, NameOnPage)));
                await Task.Delay(pointer ? DriveShowTarget : DriveStepPause, _time, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _services.Cursor.ReturnToMouse();
            var done = browser ? await BrowserStepAsync(step, cancellationToken).ConfigureAwait(false)
                : step.Kind == ComputerActionKind.Open && _services.Browser.IsConnected
                ? await _services.Browser.OpenAsync(step.Text ?? string.Empty, cancellationToken).ConfigureAwait(false)
                : _services.Control.Perform(new ComputerAction(step.Kind, x, y, step.Text));
            if (!done)
            {
                return DriveOutcome.Failed;
            }

            LogDriveStep(step.Kind);
            await Task.Delay(DriveStepPause, _time, cancellationToken).ConfigureAwait(false);
        }

        return DriveOutcome.Done;
    }

    private static bool IsBrowserStep(ComputerActionKind kind) => kind is ComputerActionKind.Press or ComputerActionKind.Fill or ComputerActionKind.Copy
        or ComputerActionKind.Paste or ComputerActionKind.PageScroll or ComputerActionKind.TabSwitch or ComputerActionKind.TabClose;

    /// <summary>A page element or tab id in words, from the page the brain last saw.</summary>
    private string? NameOnPage(string id)
        => _page?.Elements?.FirstOrDefault(element => element.Id == id) is { } element ? $"{element.Label}{(element.Kind is "button" or "link" ? string.Empty : $" {element.Kind}")}"
            : _page?.Tabs?.FirstOrDefault(tab => tab.Id == id)?.Title;

    /// <summary>
    /// One browser step through the extension. The cursor flies to what it acted on. A field that
    /// ignores a scripted value is left focused, and the text goes in with real keystrokes.
    /// </summary>
    private async Task<bool> BrowserStepAsync(ReplyItem.Act step, CancellationToken cancellationToken)
    {
        var action = step.Kind switch
        {
            ComputerActionKind.Press => "press",
            ComputerActionKind.Fill => "fill",
            ComputerActionKind.Copy => "copy",
            ComputerActionKind.Paste => "paste",
            ComputerActionKind.PageScroll => "scroll",
            ComputerActionKind.TabSwitch => "tab-switch",
            _ => "tab-close",
        };
        if (!_services.Browser.IsConnected
            || await _services.Browser.ActAsync(new Jarvis.Contracts.Browser.BrowserAct(action, step.Label, step.Text), cancellationToken).ConfigureAwait(false) is not { } result)
        {
            return false;
        }

        if (result is { X: { } x, Y: { } y })
        {
            _services.Cursor.PointAt(x, y);
        }

        if (step.Kind == ComputerActionKind.Copy && result is { Done: true, Text: { } copied })
        {
            _copied = copied;
        }

        if (!result.Done && result.Focused && (step.Kind == ComputerActionKind.Fill ? step.Text : _copied) is { Length: > 0 } text)
        {
            return _services.Control.Perform(new ComputerAction(ComputerActionKind.Type, 0, 0, text)); // real keystrokes into the focused field
        }

        if (!result.Done)
        {
            LogBrowserStepFailed(action, result.Reason ?? "unknown");
        }

        return result.Done;
    }

    /// <summary>Shows a question with two buttons in the notch; false for the second, cancellation or a minute's silence.</summary>
    private async Task<bool> ApproveAsync(string question, string yes, string no, CancellationToken cancellationToken)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = cancellationToken.Register(() => decision.TrySetResult(false));
        _services.Notch.Show(new NotchState.Response(question, [new NotchAction(yes, () => decision.TrySetResult(true)), new NotchAction(no, () => decision.TrySetResult(false))], KeepOnClickAway: true));
        var answered = await Task.WhenAny(decision.Task, Task.Delay(DriveDecisionTimeout, _time, CancellationToken.None)).ConfigureAwait(false);
        return answered == decision.Task && await decision.Task.ConfigureAwait(false);
    }

    private enum DriveOutcome
    {
        Done,
        Declined,
        Stopped,
        Failed,
    }

    private async Task EndIfStillRecordingAsync(Recording recording)
    {
        await Task.Delay(MaxRecording, _time).ConfigureAwait(false);
        bool same;
        lock (_gate)
        {
            same = ReferenceEquals(_recording, recording);
        }

        if (same)
        {
            EndListening();
        }
    }

    private void InterruptLocked()
    {
        _services.Drawing.Clear();
        _services.Cursor.SetListening(false);
        _turn?.Cancel();
        _turn?.Dispose();
        _turn = null;
        if (_recording is { } abandoned)
        {
            _recording = null;
            _ = abandoned.DisposeAsync().AsTask();
        }
    }

    private TranscriptionRequest Request()
    {
        var language = _options.CurrentValue.Language;
        return new TranscriptionRequest(string.IsNullOrWhiteSpace(language) ? null : language, []);
    }

    /// <summary>Double-tap Ctrl: stop whatever is running and open the notch's text box.</summary>
    public void Compose()
    {
        lock (_gate)
        {
            InterruptLocked();
        }

        _services.Cursor.SetThinking(false);
        _services.Sounds.Play(SoundEffect.ComposerOpen);
        _services.Notch.Show(new NotchState.Composing(
            text =>
            {
                _services.Sounds.Play(SoundEffect.ComposerSend);
                Ask(text);
            },
            () =>
            {
                _services.Sounds.Play(SoundEffect.ComposerClose);
                Cancel();
            },
            _services.History.Recent));
    }

    /// <summary>A typed request: the same turn as a spoken one, without the microphone.</summary>
    public void Ask(string request)
    {
        CancellationToken token;
        lock (_gate)
        {
            InterruptLocked();
            _turn = new CancellationTokenSource();
            token = _turn.Token;
        }

        _ = RespondAsync(request.Trim(), typed: true, token);
    }

    /// <summary>Fills the browser's form from the vault (tray menu): the same as asking to.</summary>
    public void FillForm() => Ask("Fill this form");

    private async Task RunTurnAsync(Recording recording, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReleaseTail, _time, cancellationToken).ConfigureAwait(false);
            var request = (await recording.FinishAsync(cancellationToken).ConfigureAwait(false)).Trim();
            LogHeard(request);
            await RespondAsync(request, typed: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Interrupted while finishing the recording.
        }
        catch (Exception exception)
        {
            LogTurnFailed(exception);
            _services.Notch.Show(new NotchState.Response($"Sorry, I couldn't hear that: {exception.Message}"));
        }
        finally
        {
            await recording.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <param name="typed">A typed question (it gets a chime when the answer starts) rather than a spoken one.</param>
    private async Task RespondAsync(string request, bool typed, CancellationToken cancellationToken)
    {
        var reason = typed ? "You typed a question" : "You asked by voice";
        var question = request;
        try
        {
            if (request.Length == 0)
            {
                _services.Notch.Show(NotchState.Idle);
                return;
            }

            var privacy = _services.Privacy;
            if (_services.Local is { } local && await AnswerOnThisPcAsync(local, request, typed, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            _services.Notch.Show(new NotchState.Thinking(privacy.ScreenAccess && privacy.ShowWhenLooking ? "Looking at your screen" : null));
            _services.Cursor.SetThinking(true);
            var started = _time.GetTimestamp();
            IReadOnlyList<CapturedScreen> screens = [];
            if (privacy.ScreenAccess)
            {
                screens = await _services.ScreenCapture.CaptureAsync(cancellationToken).ConfigureAwait(false);
                privacy.Record(screens.Count, reason);
                if (_services.Cursor.TakeHighlight() is { } highlight && CompanionPrompt.DescribeHighlight(highlight, screens) is { } marked)
                {
                    request = marked + request;
                }

                LogCaptured(screens.Count, _time.GetElapsedTime(started).TotalMilliseconds);
            }
            else
            {
                request = CompanionPrompt.ScreenOff + request;
            }

            // A browser in front and the extension paired: read that tab too (logged like a screenshot).
            if (privacy.ScreenAccess && _services.BrowserOptions.CurrentValue.ReadPage && _services.Browser.IsConnected && _services.Browser.IsBrowserInFront
                && await _services.Browser.ReadPageAsync(cancellationToken).ConfigureAwait(false) is { } page)
            {
                privacy.Record(0, "Read the page open in your browser");
                request = CompanionPrompt.DescribePage(page) + request;
                _page = page;
            }

            var mayDrive = privacy.ScreenAccess && privacy.LetJarvisDrive;
            if (mayDrive)
            {
                request = CompanionPrompt.DriveOn + request;
            }

            var (answer, steps, more, _) = await AnswerAsync(_services.Brain, CompanionPrompt.Instructions, request, screens, started, typed, cancellationToken).ConfigureAwait(false);
            _services.History.Add(question, answer);
            if (_formWanted is { } wanted && _services.Local is { } forms)
            {
                // Claude saw a form to fill: hand it back to this PC, where the vault is.
                _formWanted = null;
                privacy.Record(0, "Read the form in your browser and answered it on this PC");
                var fillIn = wanted.FillIn || TurnRouter.WantsFillIn(question);
                await forms.Forms.RunAsync(fillIn, cancellationToken, () => Ask(fillIn ? "Fill this form for me" : "Fill this form")).ConfigureAwait(false);
                return;
            }

            if (mayDrive && steps.Count > 0)
            {
                await DriveTaskAsync(question, steps, more, screens, cancellationToken).ConfigureAwait(false);
            }
            LogAnswered(_time.GetElapsedTime(started).TotalMilliseconds);

            await Task.Delay(TimeSpan.FromSeconds(_options.CurrentValue.CaptionSeconds), _time, cancellationToken).ConfigureAwait(false);
            _services.Notch.Show(NotchState.Idle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Interrupted by a new press or Escape; whoever interrupted owns the UI now.
        }
        catch (Exception exception)
        {
            LogTurnFailed(exception);
            _services.Notch.Show(new NotchState.Response($"Sorry, that didn't work: {exception.Message}"));
        }
        finally
        {
            _services.Cursor.SetThinking(false);
        }
    }

    /// <summary>
    /// Turns that stay on this PC: forms and vault questions always, everything else in private
    /// mode. False when the turn should go on to Claude after all: private mode's local model
    /// needed the screen and the user agreed to send a screenshot.
    /// </summary>
    private async Task<bool> AnswerOnThisPcAsync(LocalTurns local, string request, bool typed, CancellationToken cancellationToken)
    {
        var privacy = _services.Privacy;
        var browser = _services.Browser;
        var route = TurnRouter.Route(request, privacy.PrivateMode, browser.IsBrowserInFront);

        // Misheard ("feel this farm")? If it sounds form-ish and the page in front has a form, it's that.
        if (route is TurnRoute.Cloud or TurnRoute.Local && browser.IsConnected && browser.IsBrowserInFront && TurnRouter.MightMeanForm(request)
            && await browser.ReadFormAsync(cancellationToken).ConfigureAwait(false) is { Questions.Count: > 0 })
        {
            route = TurnRoute.Form;
        }

        LogRoute(route);
        switch (route)
        {
            case TurnRoute.Cloud:
                return false;

            case TurnRoute.Form:
                privacy.Record(0, "Read the form in your browser and answered it on this PC");
                var fillIn = TurnRouter.WantsFillIn(request);
                await local.Forms.RunAsync(fillIn, cancellationToken, () => Ask(fillIn ? "Fill this form for me" : "Fill this form")).ConfigureAwait(false);
                return true;

            case TurnRoute.Vault when !local.Vault.IsConnected:
                _services.Notch.Show(new NotchState.Response("That needs your vault, and Jarvis isn't connected to it yet. Connect it in Settings → Vault."));
                return true;
        }

        var vault = route == TurnRoute.Vault;
        privacy.Record(0, vault ? "Answered from your vault on this PC" : "Answered on this PC (private mode)");
        _services.Notch.Show(new NotchState.Thinking(vault ? "Looking in your vault" : null));
        _services.Cursor.SetThinking(true);
        var started = _time.GetTimestamp();
        var (answer, _, _, needScreen) = vault
            ? await AnswerAsync(local.VaultBrain, string.Empty, request, [], started, typed, cancellationToken).ConfigureAwait(false)
            : await AnswerAsync(local.LocalBrain, CompanionPrompt.PrivateInstructions, request, [], started, typed, cancellationToken).ConfigureAwait(false);
        _services.Cursor.SetThinking(false);
        if (needScreen && !vault)
        {
            if (privacy.ScreenAccess && await ApproveAsync(
                    "Private mode is on, and this needs a look at your screen. Only Claude can see images: send it a screenshot for this question?", "Send to Claude", "No", cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            answer = "Okay, I kept it on this PC. Tell me what you see and I'll help from that.";
            _services.Notch.Show(new NotchState.Response(answer));
        }

        _services.History.Add(request, answer);
        await Task.Delay(TimeSpan.FromSeconds(_options.CurrentValue.CaptionSeconds), _time, cancellationToken).ConfigureAwait(false);
        _services.Notch.Show(NotchState.Idle);
        return true;
    }

    /// <returns>The answer as captioned (what was said, without tags or drawings), any mouse/keyboard steps, and whether the brain needs the screen.</returns>
    private async Task<(string Answer, List<ReplyItem.Act> Steps, bool More, bool NeedScreen)> AnswerAsync(
        IAssistantBrain brain, string instructions, string request, IReadOnlyList<CapturedScreen> screens, long started, bool typed, CancellationToken cancellationToken)
    {
        var images = screens
            .Select((screen, index) => new LabeledImage(
                CompanionPrompt.DescribeScreen(index + 1, screens.Count, screen.Image.Width, screen.Image.Height, screen.Image.HasCursor),
                screen.Image.Jpeg))
            .ToList();
        var turn = new BrainTurn(instructions, request, images);

        var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var speaking = SpeakAsync(lines.Reader, cancellationToken);
        var parser = new ReplyParser();
        var captions = new StringBuilder();
        var raw = new StringBuilder();
        var steps = new List<ReplyItem.Act>();
        var more = false;
        var needScreen = false;

        try
        {
            await foreach (var brainEvent in brain.RunTurnAsync(turn, cancellationToken).ConfigureAwait(false))
            {
                if (brainEvent is BrainEvent.TextDelta delta)
                {
                    raw.Append(delta.Text);
                    Handle(parser.Feed(delta.Text));
                }
            }

            Handle(parser.Complete());
            LogReply(raw.ToString());
        }
        finally
        {
            lines.Writer.TryComplete();
        }

        await speaking.ConfigureAwait(false);
        return (captions.ToString(), steps, more, needScreen);

        void Handle(IReadOnlyList<ReplyItem> items)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case ReplyItem.Line line:
                        if (captions.Length == 0)
                        {
                            _services.Cursor.SetThinking(false);
                            LogFirstSentence(_time.GetElapsedTime(started).TotalMilliseconds);
                            if (typed)
                            {
                                _services.Sounds.Play(SoundEffect.AnswerReady);
                            }

                        }

                        captions.Append(captions.Length == 0 ? string.Empty : " ").Append(line.Text);
                        _services.Notch.Show(new NotchState.Response(captions.ToString()));
                        lines.Writer.TryWrite(line.Text);
                        break;

                    case ReplyItem.Drawing drawing:
                        var target = screens.ElementAtOrDefault(drawing.Script.Screen - 1) ?? screens.FirstOrDefault();
                        if (target is not null && drawing.Script.Commands.Count > 0)
                        {
                            var ink = CursorColor.Parse(_cursorOptions.CurrentValue.Color);
                            _services.Drawing.Show(SceneBuilder.Build(drawing.Script, target, ink, (float)target.Scale, Random.Shared.Next()));
                            _services.Sounds.Play(SoundEffect.Drawing);
                        }

                        break;

                    case ReplyItem.Act step:
                        steps.Add(step);
                        break;

                    case ReplyItem.More:
                        more = true;
                        break;

                    case ReplyItem.NeedScreen:
                        needScreen = true;
                        break;

                    case ReplyItem.FillForm form:
                        _formWanted = form;
                        break;

                    case ReplyItem.Delegate job:
                        _services.Agents.Start(job.Brief);
                        break;

                    case ReplyItem.Recurring recurring when RoutineSchedule.IsValid(recurring.Schedule):
                        _services.Routines.Add(new Routine(Guid.NewGuid().ToString("N"), recurring.Schedule, recurring.Brief, _time.GetUtcNow(), null));
                        break;

                    case ReplyItem.Recurring recurring:
                        LogBadSchedule(recurring.Schedule);
                        break;

                    case ReplyItem.Point point:
                        var screen = screens.ElementAtOrDefault(point.Screen - 1) ?? screens.FirstOrDefault();
                        if (screen is not null)
                        {
                            var (x, y) = screen.ToDesktop(point.X, point.Y);
                            _services.Cursor.PointAt(x, y, point.Label);
                        }

                        break;
                }
            }
        }
    }

    private Task SpeakAsync(ChannelReader<string> lines, CancellationToken cancellationToken)
    {
        var tts = _services.TextToSpeech;
        var style = new SpeechStyle(_options.CurrentValue.Voice, _options.CurrentValue.Speed);
        return _services.Player.PlayAsync(Synthesize(lines, tts, style, cancellationToken), tts.OutputFormat, cancellationToken);

        static async IAsyncEnumerable<ReadOnlyMemory<byte>> Synthesize(
            ChannelReader<string> lines,
            ITextToSpeech tts,
            SpeechStyle style,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var line in lines.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await foreach (var chunk in tts.SynthesizeAsync(line, style, cancellationToken).ConfigureAwait(false))
                {
                    yield return chunk;
                }
            }
        }
    }

    // Who answered, never what was asked.
    [LoggerMessage(Level = LogLevel.Information, Message = "Turn routed to {Route}")]
    private partial void LogRoute(Jarvis.Core.Vault.TurnRoute route);

    [LoggerMessage(Level = LogLevel.Information, Message = "Browser step {Action} failed: {Reason}")]
    private partial void LogBrowserStepFailed(string action, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Did an approved {Kind} step")]
    private partial void LogDriveStep(ComputerActionKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored a routine with an unknown schedule: {Schedule}")]
    private partial void LogBadSchedule(string schedule);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured {Count} screen(s) in {Milliseconds:F0} ms")]
    private partial void LogCaptured(int count, double milliseconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "First sentence ready {Milliseconds:F0} ms after release")]
    private partial void LogFirstSentence(double milliseconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer finished speaking {Milliseconds:F0} ms after release")]
    private partial void LogAnswered(double milliseconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reply: {Reply}")]
    private partial void LogReply(string reply);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engines ready")]
    private partial void LogReady();

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening")]
    private partial void LogListening();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Heard: \"{Request}\"")]
    private partial void LogHeard(string request);

    [LoggerMessage(Level = LogLevel.Error, Message = "Voice turn failed")]
    private partial void LogTurnFailed(Exception exception);
}

/// <summary>Everything a voice turn talks to, grouped so the session's constructor stays readable.</summary>
public sealed record CompanionServices(
    IMicrophone Microphone,
    ISpeechToText SpeechToText,
    IScreenCapture ScreenCapture,
    IAssistantBrain Brain,
    ITextToSpeech TextToSpeech,
    IAudioPlayer Player,
    ICursorOverlay Cursor,
    IScreenDrawing Drawing,
    INotchPresenter Notch,
    IAgentJobs Agents,
    ISoundEffects Sounds,
    IRoutineStore Routines,
    ScreenPrivacy Privacy,
    ConversationLog History,
    IComputerControl Control,
    Jarvis.Contracts.Browser.IBrowserLink Browser,
    IOptionsMonitor<BrowserOptions> BrowserOptions,
    LocalTurns? Local = null);

/// <summary>What answers on this PC: the vault (its brain and form filling) and the local model for private mode.</summary>
public sealed record LocalTurns(Jarvis.Contracts.Vault.IVault Vault, IAssistantBrain VaultBrain, IAssistantBrain LocalBrain, FormFiller Forms);
