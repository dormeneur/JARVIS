using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace Jarvis.Providers.Whisper;

/// <summary>
/// Local, free speech recognition with whisper.cpp. Audio is buffered while the key is held and
/// transcribed in one pass on release (push-to-talk utterances are short). The model downloads
/// once, on first use, into <see cref="WhisperOptions.ModelDirectory"/>.
/// </summary>
public sealed partial class WhisperSpeechToText : ISpeechToText, IUnloadable, IDisposable
{
    /// <summary>Shorter than this is a key tap, not speech; Whisper would only hallucinate on it.</summary>
    private static readonly TimeSpan MinimumSpeech = TimeSpan.FromMilliseconds(300);

    private readonly WhisperOptions _options;
    private readonly ILogger<WhisperSpeechToText> _logger;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private WhisperFactory? _factory;

    public WhisperSpeechToText(IOptions<WhisperOptions> options, ILogger<WhisperSpeechToText> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
    }

    public AudioFormat InputFormat => AudioFormat.SpeechMono16K;

    public async Task<ITranscriptionSession> StartSessionAsync(TranscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var factory = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return new Session(factory, request);
    }

    /// <summary>Downloads (first run) and loads the model so the first utterance isn't slow.</summary>
    public Task PrewarmAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    /// <summary>Frees the model (about 300 MB of native memory). Callers make sure no session is running.</summary>
    public void Unload()
    {
        _loading.Wait();
        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _loading.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _loading.Dispose();
    }

    private async Task<WhisperFactory> LoadAsync(CancellationToken cancellationToken)
    {
        if (_factory is { } loaded)
        {
            return loaded;
        }

        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_factory is null)
            {
                var path = await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
                _factory = WhisperFactory.FromPath(path);
                LogLoaded(_options.Model);
            }

            return _factory;
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_options.ModelDirectory, $"ggml-{_options.Model}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        if (!Enum.TryParse<GgmlType>(_options.Model.Replace(".", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var type))
        {
            throw new InvalidOperationException($"Unknown Whisper model '{_options.Model}'. Try base, base.en, small or small.en.");
        }

        LogDownloading(_options.Model, path);
        Directory.CreateDirectory(_options.ModelDirectory);
        var temporary = path + ".download";
        await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(type, cancellationToken: cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(temporary))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
        return path;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading Whisper model {Model} to {Path}")]
    private partial void LogDownloading(string model, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Whisper model {Model} loaded")]
    private partial void LogLoaded(string model);

    private sealed partial class Session(WhisperFactory factory, TranscriptionRequest request) : ITranscriptionSession
    {
        private readonly MemoryStream _audio = new();

        public ValueTask WriteAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken)
        {
            _audio.Write(pcm16.Span);
            return ValueTask.CompletedTask;
        }

        public async Task<string> CompleteAsync(CancellationToken cancellationToken)
        {
            var samples = ToFloats(_audio.GetBuffer().AsSpan(0, (int)_audio.Length));
            if (samples.Length < AudioFormat.SpeechMono16K.SampleRate * MinimumSpeech.TotalSeconds)
            {
                return string.Empty;
            }

            var builder = factory.CreateBuilder().WithLanguage(request.Language ?? "auto");
            if (request.Vocabulary.Count > 0)
            {
                builder = builder.WithPrompt(string.Join(", ", request.Vocabulary));
            }

            await using var processor = builder.Build();
            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }

            return NonSpeech().Replace(text.ToString(), string.Empty).Trim();
        }

        public ValueTask DisposeAsync() => _audio.DisposeAsync();

        private static float[] ToFloats(ReadOnlySpan<byte> pcm16)
        {
            var shorts = MemoryMarshal.Cast<byte, short>(pcm16);
            var samples = new float[shorts.Length];
            for (var i = 0; i < shorts.Length; i++)
            {
                samples[i] = shorts[i] / 32768f;
            }

            return samples;
        }

        /// <summary>Whisper's annotations for non-speech, e.g. [BLANK_AUDIO], (music), [typing].</summary>
        [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
        private static partial Regex NonSpeech();
    }
}

public sealed class WhisperOptions
{
    public const string SectionName = "SpeechToText:Whisper";

    /// <summary>ggml model name: tiny, base, small, medium (append .en for English-only variants).</summary>
    public string Model { get; set; } = "base";

    /// <summary>Where models are stored; the app sets this to its data folder.</summary>
    public string ModelDirectory { get; set; } = "models";
}
