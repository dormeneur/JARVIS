using System.Runtime.CompilerServices;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Speech;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Providers;

/// <summary>Memory settings, bound from the "Memory" settings section.</summary>
public sealed class MemoryOptions
{
    public const string SectionName = "Memory";

    /// <summary>Free the voice models after this many idle minutes; 0 keeps them loaded (fastest replies).</summary>
    public int UnloadVoiceAfterMinutes { get; set; }
}

/// <summary>
/// Frees the speech engines' models once nothing has used them for the configured time. Every use
/// goes through the wrappers below, which count work in flight, so a model is never freed mid-use.
/// The next use reloads it (a few seconds).
/// </summary>
public sealed class IdleEngines : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IUnloadable> _engines;
    private readonly IOptionsMonitor<MemoryOptions> _options;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private int _active;
    private DateTimeOffset _lastUsed;
    private bool _loaded = true;

    public IdleEngines(IEnumerable<object> engines, IOptionsMonitor<MemoryOptions> options, TimeProvider time)
    {
        _engines = [.. engines.OfType<IUnloadable>()];
        _options = options;
        _time = time;
        _lastUsed = time.GetUtcNow();
        _timer = time.CreateTimer(_ => Check(), null, CheckEvery, CheckEvery);
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>Marks work starting; dispose the result when it ends.</summary>
    public IDisposable Use()
    {
        lock (_gate)
        {
            _active++;
            _loaded = true;
            _lastUsed = _time.GetUtcNow();
        }

        return new Lease(this);
    }

    /// <summary>Frees the models if they've been idle long enough. Called by the timer; public for tests.</summary>
    public void Check()
    {
        var minutes = _options.CurrentValue.UnloadVoiceAfterMinutes;
        lock (_gate)
        {
            if (minutes <= 0 || !_loaded || _active > 0 || _time.GetUtcNow() - _lastUsed < TimeSpan.FromMinutes(minutes))
            {
                return;
            }

            _loaded = false;
            foreach (var engine in _engines)
            {
                engine.Unload();
            }
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _active--;
            _lastUsed = _time.GetUtcNow();
        }
    }

    private sealed class Lease(IdleEngines owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}

/// <summary>Speech-to-text that reports its use to <see cref="IdleEngines"/>.</summary>
public sealed class TrackedSpeechToText(ISpeechToText inner, IdleEngines idle) : ISpeechToText
{
    public AudioFormat InputFormat => inner.InputFormat;

    public async Task<ITranscriptionSession> StartSessionAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        var lease = idle.Use();
        try
        {
            return new Session(await inner.StartSessionAsync(request, cancellationToken).ConfigureAwait(false), lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public Task PrewarmAsync(CancellationToken cancellationToken) => inner.PrewarmAsync(cancellationToken);

    private sealed class Session(ITranscriptionSession inner, IDisposable lease) : ITranscriptionSession
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken) => inner.WriteAsync(pcm16, cancellationToken);

        public Task<string> CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }
}

/// <summary>Text-to-speech that reports its use to <see cref="IdleEngines"/>.</summary>
public sealed class TrackedTextToSpeech(ITextToSpeech inner, IdleEngines idle) : ITextToSpeech
{
    public AudioFormat OutputFormat => inner.OutputFormat;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, SpeechStyle style, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = idle.Use();
        await foreach (var chunk in inner.SynthesizeAsync(text, style, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public Task PrewarmAsync(CancellationToken cancellationToken) => inner.PrewarmAsync(cancellationToken);
}
