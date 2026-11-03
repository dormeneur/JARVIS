using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Notch;
using Jarvis.Contracts.Speech;
using Jarvis.Core.Companion;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Agents;

/// <summary>
/// Tells the user about background jobs: a chime when one starts, and when it ends a chime, the
/// result in the notch and its first sentence spoken (the voice reply already said "I'm on it").
/// </summary>
public sealed partial class AgentAnnouncer : IDisposable
{
    private readonly IAgentJobs _jobs;
    private readonly INotchPresenter _notch;
    private readonly ITextToSpeech _speech;
    private readonly IAudioPlayer _player;
    private readonly IOptionsMonitor<CompanionOptions> _voice;
    private readonly TimeProvider _time;
    private readonly ISoundEffects _sounds;
    private readonly IOptionsMonitor<AgentOptions> _options;

    public AgentAnnouncer(IAgentJobs jobs, INotchPresenter notch, ITextToSpeech speech, IAudioPlayer player, IOptionsMonitor<CompanionOptions> voice, TimeProvider time, ISoundEffects sounds, IOptionsMonitor<AgentOptions> options)
    {
        _options = options;
        _sounds = sounds;
        _jobs = jobs;
        _notch = notch;
        _speech = speech;
        _player = player;
        _voice = voice;
        _time = time;
        _jobs.Changed += OnChanged;
    }

    public void Dispose() => _jobs.Changed -= OnChanged;

    /// <summary>Text without Markdown punctuation, for the notch and the voice (neither renders Markdown).</summary>
    internal static string Plain(string text) => Markdown().Replace(text, string.Empty).Trim();

    /// <summary>The first sentence of a result, for reading aloud.</summary>
    internal static string FirstSentence(string text)
    {
        var plain = Plain(text);
        var end = SentenceEnd().Match(plain);
        return end.Success ? plain[..(end.Index + end.Length)].Trim() : plain;
    }

    private void OnChanged(AgentJobInfo job)
    {
        switch (job.Status)
        {
            case AgentJobStatus.Queued:
                _sounds.Play(SoundEffect.JobStarted);
                break;
            case AgentJobStatus.Finished:
                _sounds.Play(SoundEffect.JobDone);
                _ = AnnounceAsync(job.Result ?? "Done.", Plain($"{job.Title}: {job.Result}"), Path.Combine(job.Folder, "output"));
                break;
            case AgentJobStatus.Failed:
                _sounds.Play(SoundEffect.JobFailed);
                _ = AnnounceAsync($"I couldn't finish {job.Title}.", Plain($"{job.Title} didn't finish: {job.Result}"), job.Folder);
                break;
        }
    }

    private Task AnnounceAsync(string spoken, string caption, string folder)
        => SayAsync(caption, _options.CurrentValue.SpeakResults ? FirstSentence(spoken) : null, [new("Open", () => OpenFolder(folder)), new("Dismiss", () => _notch.Show(NotchState.Idle))]);

    /// <summary>
    /// Speaks up unprompted: <paramref name="caption"/> and its buttons in the notch, <paramref name="spoken"/>
    /// read aloud if given, then back to rest once the caption has had its time.
    /// </summary>
    public async Task SayAsync(string caption, string? spoken, IReadOnlyList<NotchAction> actions)
    {
        var voice = _voice.CurrentValue;
        var shown = new NotchState.Response(caption, actions);
        _notch.Show(shown);
        if (spoken is not null)
        {
            await _player.PlayAsync(Speak(spoken, new SpeechStyle(voice.Voice, voice.Speed)), _speech.OutputFormat, CancellationToken.None).ConfigureAwait(false);
        }

        await Task.Delay(TimeSpan.FromSeconds(voice.CaptionSeconds), _time).ConfigureAwait(false);
        if (ReferenceEquals(_notch.Current, shown))
        {
            _notch.Show(NotchState.Idle); // unless something else took the notch meanwhile
        }
    }

    /// <summary>Opens a folder in the system's file browser.</summary>
    private static void OpenFolder(string folder)
    {
        if (Directory.Exists(folder))
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> Speak(string text, SpeechStyle style, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _speech.SynthesizeAsync(text, style, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    [GeneratedRegex(@"[*_`#>\[\]]")]
    private static partial Regex Markdown();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();
}

/// <summary>
/// Wraps the notch so "idle" means "idle, unless background work is running": then the sliver
/// shows the working light. Everything else passes straight through.
/// </summary>
public sealed class AmbientNotch : INotchPresenter, IDisposable
{
    private readonly INotchPresenter _inner;
    private readonly IAgentJobs _jobs;
    private readonly Lock _gate = new();
    private NotchState _requested = NotchState.Idle;

    public AmbientNotch(INotchPresenter inner, IAgentJobs jobs)
    {
        _inner = inner;
        _jobs = jobs;
        _jobs.Changed += OnJobChanged;
    }

    public void Show(NotchState state)
    {
        lock (_gate)
        {
            _requested = state;
        }

        _inner.Show(Resolve(state));
    }

    public NotchState Current
    {
        get
        {
            lock (_gate)
            {
                return _requested;
            }
        }
    }

    public void Dispose() => _jobs.Changed -= OnJobChanged;

    private NotchState Resolve(NotchState state)
        => state is NotchState.IdleState && _jobs.Jobs.Any(job => job.Status is AgentJobStatus.Queued or AgentJobStatus.Running)
            ? new NotchState.Working()
            : state;

    /// <summary>A job started or ended while the notch was resting: switch between idle and working.</summary>
    private void OnJobChanged(AgentJobInfo job)
    {
        NotchState requested;
        lock (_gate)
        {
            requested = _requested;
        }

        if (requested is NotchState.IdleState)
        {
            _inner.Show(Resolve(requested));
        }
    }
}
