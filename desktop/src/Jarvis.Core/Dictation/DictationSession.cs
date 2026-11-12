using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Speech;
using Jarvis.Contracts.Text;
using Jarvis.Core.Companion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Dictation;

/// <summary>
/// Hold to dictate: remember the focused field, record while the key is held, transcribe, clean up,
/// and type the result into that field. Thread-safe; a new hold interrupts the previous one.
/// Dictated text is never logged, only its length.
/// </summary>
public sealed partial class DictationSession : IAsyncDisposable
{
    private static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(150);

    /// <summary>How long after typing we watch for the user fixing a word, and how often we look.</summary>
    private static readonly TimeSpan CorrectionWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CorrectionPoll = TimeSpan.FromSeconds(1);

    private readonly DictationServices _services;
    private readonly DictationCleaner _cleaner;
    private readonly IOptionsMonitor<DictationOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DictationSession> _logger;
    private readonly Lock _gate = new();
    private Recording? _recording;
    private Task<IFocusedField?>? _focus;
    private CancellationTokenSource? _turn;

    public DictationSession(DictationServices services, IOptionsMonitor<DictationOptions> options, TimeProvider time, ILogger<DictationSession> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _cleaner = new DictationCleaner(services.CleanupBrain);
        _options = options;
        _time = time;
        _logger = logger;
    }

    public void BeginDictation()
    {
        try
        {
            StartRecordingLocked();
        }
        catch (MicrophoneBlockedException)
        {
            _services.Cursor.SetListening(false);
            _services.Notch.Show(new NotchState.Response(
                MicrophoneBlockedException.Advice,
                [new NotchAction(MicrophoneBlockedException.SettingsLabel, () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MicrophoneBlockedException.SettingsPage) { UseShellExecute = true })?.Dispose()), new NotchAction("Dismiss", () => _services.Notch.Show(NotchState.Idle))]));
            return;
        }

        _services.Cursor.SetListening(true);
        _services.Notch.Show(new NotchState.Dictating());
    }

    private void StartRecordingLocked()
    {
        lock (_gate)
        {
            InterruptLocked();
            var options = _options.CurrentValue;
            var language = string.IsNullOrWhiteSpace(options.Language) ? null : options.Language;
            _recording = Recording.Start(_services.Microphone, _services.SpeechToText, new TranscriptionRequest(language, Words(options)), _services.Cursor.ShowLevel, _logger);
            _focus = _services.Insertion.CaptureFocusAsync(CancellationToken.None);
            _ = EndIfStillRecordingAsync(_recording);
        }
    }

    /// <summary>The longest recording (DESIGN-FACTS.md §4); hands-free and click-to-talk end here by themselves.</summary>
    internal TimeSpan MaxRecording { get; init; } = TimeSpan.FromSeconds(120);

    public void EndDictation()
    {
        Recording? recording;
        Task<IFocusedField?>? focus;
        CancellationToken token;
        lock (_gate)
        {
            (recording, focus) = (_recording, _focus);
            (_recording, _focus) = (null, null);
            if (recording is null || focus is null)
            {
                return;
            }

            _turn = new CancellationTokenSource();
            token = _turn.Token;
        }

        _services.Cursor.SetListening(false);

        _ = RunAsync(recording, focus, token);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            InterruptLocked();
        }

        _services.Notch.Show(NotchState.Idle);
    }

    /// <summary>Types already-transcribed text, as if it had just been dictated (text mode, tests).</summary>
    public async Task<InsertOutcome> InsertTranscriptAsync(string raw, CancellationToken cancellationToken)
    {
        var field = await _services.Insertion.CaptureFocusAsync(cancellationToken).ConfigureAwait(false);
        return await DeliverAsync(raw, field, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Raised when a word is learned from the user's correction (for tests and diagnostics).</summary>
    public event Action<Correction>? WordLearned;

    public Task PrewarmAsync(CancellationToken cancellationToken)
        => _options.CurrentValue.Cleanup
            ? _services.CleanupBrain.PrewarmAsync(DictationCleaner.Instructions, cancellationToken)
            : Task.CompletedTask;

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
            EndDictation();
        }
    }

    private void InterruptLocked()
    {
        _services.Cursor.SetListening(false);
        _turn?.Cancel();
        _turn?.Dispose();
        _turn = null;
        _focus = null;
        if (_recording is { } abandoned)
        {
            _recording = null;
            _ = abandoned.DisposeAsync().AsTask();
        }
    }

    private async Task RunAsync(Recording recording, Task<IFocusedField?> focus, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReleaseTail, _time, cancellationToken).ConfigureAwait(false);
            var raw = (await recording.FinishAsync(cancellationToken).ConfigureAwait(false)).Trim();
            if (raw.Length == 0)
            {
                _services.Notch.Show(NotchState.Idle);
                return;
            }

            await DeliverAsync(raw, await focus.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Interrupted; whoever interrupted owns the UI.
        }
        catch (Exception exception)
        {
            LogFailed(exception);
            _services.Notch.Show(new NotchState.Response($"Dictation didn't work: {exception.Message}"));
        }
        finally
        {
            await recording.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<InsertOutcome> DeliverAsync(string raw, IFocusedField? field, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var started = _time.GetTimestamp();
        var text = raw;
        // Clean-up goes to Claude: text with a password or key in it is inserted as heard instead.
        if (options.Cleanup && Jarvis.Core.Assistant.OutboundScanner.Scan(raw).Count == 0)
        {
            _services.Notch.Show(new NotchState.Working());
            text = await _cleaner.CleanAsync(raw, field, Words(options), cancellationToken).ConfigureAwait(false);
        }

        var cleaned = _time.GetElapsedTime(started).TotalMilliseconds;
        var outcome = await _services.Insertion.InsertAsync(field, text, cancellationToken).ConfigureAwait(false);
        LogDelivered(raw.Length, text.Length, field?.AppName ?? "(no field)", outcome, cleaned, _time.GetElapsedTime(started).TotalMilliseconds);

        if (outcome == InsertOutcome.CopiedToClipboard)
        {
            _services.Notch.Show(new NotchState.Response($"Copied, press Ctrl+V to paste: {text}"));
            await Task.Delay(TimeSpan.FromSeconds(options.NoticeSeconds), _time, cancellationToken).ConfigureAwait(false);
        }

        _services.Notch.Show(NotchState.Idle);
        if (outcome == InsertOutcome.Inserted && field is not null)
        {
            _ = WatchForCorrectionAsync(field, text, cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// For a little while after typing, notices the user fixing one of the dictated words (a name
    /// Whisper misheard, say) and remembers the fixed word for next time. Waits for the field to
    /// stop changing before judging, so half-typed corrections aren't learned.
    /// </summary>
    private async Task WatchForCorrectionAsync(IFocusedField field, string inserted, CancellationToken cancellationToken)
    {
        try
        {
            var baseline = await _services.Insertion.ReadAsync(field, cancellationToken).ConfigureAwait(false);
            if (baseline is null)
            {
                LogWatchStopped("the field can't be read");
                return;
            }

            var previous = baseline;
            for (var waited = TimeSpan.Zero; baseline is not null && waited < CorrectionWindow; waited += CorrectionPoll)
            {
                await Task.Delay(CorrectionPoll, _time, cancellationToken).ConfigureAwait(false);
                var now = await _services.Insertion.ReadAsync(field, cancellationToken).ConfigureAwait(false);
                if (now is null)
                {
                    LogWatchStopped("the field can no longer be read");
                    return;
                }

                var settled = now == previous && now != baseline;
                previous = now;
                if (!settled)
                {
                    continue;
                }

                if (CorrectionDetector.Find(inserted, baseline, now) is not { } correction)
                {
                    LogEditNotACorrection();
                    continue;
                }

                if (_services.Vocabulary.Add(correction.Corrected))
                {
                    WordLearned?.Invoke(correction);
                    LogLearned(correction.Corrected.Length);
                    _services.Notch.Show(new NotchState.Response($"Learned “{correction.Corrected}” for next time."));
                    await Task.Delay(TimeSpan.FromSeconds(_options.CurrentValue.NoticeSeconds), _time, cancellationToken).ConfigureAwait(false);
                    _services.Notch.Show(NotchState.Idle);
                }

                return;
            }
        }
        catch (OperationCanceledException)
        {
            // A new dictation started; it watches its own text.
        }
        catch (Exception exception)
        {
            // Fire-and-forget: report here or the failure vanishes.
            LogWatchFailed(exception);
        }
    }

    private static List<string> Words(DictationOptions options)
        => [.. options.Words.Where(word => !string.IsNullOrWhiteSpace(word)).Take(DictationOptions.MaximumWords)];

    [LoggerMessage(Level = LogLevel.Information, Message = "Dictated {RawLength} → {TextLength} chars into {App}: {Outcome} (clean-up {CleanupMs:F0} ms, total {TotalMs:F0} ms)")]
    private partial void LogDelivered(int rawLength, int textLength, string app, InsertOutcome outcome, double cleanupMs, double totalMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopped watching for corrections: {Reason}")]
    private partial void LogWatchStopped(string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The field changed, but not as a correction of a dictated word")]
    private partial void LogEditNotACorrection();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Watching for corrections failed")]
    private partial void LogWatchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Learned a {Length}-character word from a correction")]
    private partial void LogLearned(int length);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dictation failed")]
    private partial void LogFailed(Exception exception);
}

/// <summary>Everything dictation talks to.</summary>
/// <param name="CleanupBrain">A fast model used only for tidying transcripts.</param>
public sealed record DictationServices(
    IMicrophone Microphone,
    ISpeechToText SpeechToText,
    ITextInsertion Insertion,
    Jarvis.Contracts.Brain.IAssistantBrain CleanupBrain,
    INotchPresenter Notch,
    IVocabularyStore Vocabulary,
    Jarvis.Contracts.Overlay.ICursorOverlay Cursor);
