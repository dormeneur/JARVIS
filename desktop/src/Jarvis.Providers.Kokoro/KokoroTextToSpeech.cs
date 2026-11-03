using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Speech;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Providers.Kokoro;

/// <summary>
/// Local, free neural speech with Kokoro-82M on ONNX Runtime. Synthesises one sentence at a time;
/// the caller plays sentence N while this renders N+1. The model downloads once, on first use.
/// </summary>
public sealed partial class KokoroTextToSpeech : ITextToSpeech, IUnloadable, IDisposable
{
    private const string ModelUrl = "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx";
    private const string DefaultVoice = "af_heart";

    /// <summary>The model's context limit, minus room for its start/end tokens.</summary>
    private const int MaximumTokens = 500;

    private readonly KokoroOptions _options;
    private readonly ILogger<KokoroTextToSpeech> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _engine = new(1, 1);
    private KokoroModel? _model;

    public KokoroTextToSpeech(IOptions<KokoroOptions> options, HttpClient http, ILogger<KokoroTextToSpeech> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _http = http;
        _logger = logger;
    }

    public AudioFormat OutputFormat { get; } = new(24_000, 1);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, SpeechStyle style, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(style);
        var model = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var voice = Voice(style.Voice);
        var tokens = Tokenizer.Tokenize(text, voice.Name.StartsWith('b') ? "en-gb" : "en-us");

        for (var start = 0; start < tokens.Length; start += MaximumTokens)
        {
            var window = tokens[start..Math.Min(tokens.Length, start + MaximumTokens)];
            var samples = await InferAsync(model, window, voice, (float)style.Speed, cancellationToken).ConfigureAwait(false);
            yield return ToPcm16(samples);
        }
    }

    /// <summary>Downloads (first run) and loads the model so the first reply isn't slow.</summary>
    public Task PrewarmAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    /// <summary>Frees the model; waits for any sentence being rendered.</summary>
    public void Unload()
    {
        _engine.Wait();
        try
        {
            _model?.Dispose();
            _model = null;
        }
        finally
        {
            _engine.Release();
        }
    }

    public void Dispose()
    {
        _model?.Dispose();
        _engine.Dispose();
    }

    private async Task<float[]> InferAsync(KokoroModel model, int[] tokens, KokoroVoice voice, float speed, CancellationToken cancellationToken)
    {
        // The ONNX session is not safe for concurrent runs; inference is CPU-bound, so keep it off the caller's thread.
        await _engine.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => model.Infer(tokens, voice.Features, speed), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _engine.Release();
        }
    }

    private static KokoroVoice Voice(string name)
    {
        try
        {
            return KokoroVoiceManager.GetVoice(name);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or InvalidOperationException)
        {
            return KokoroVoiceManager.GetVoice(DefaultVoice);
        }
    }

    private async Task<KokoroModel> LoadAsync(CancellationToken cancellationToken)
    {
        if (_model is { } loaded)
        {
            return loaded;
        }

        await _engine.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_model is null)
            {
                var path = await EnsureModelAsync(cancellationToken).ConfigureAwait(false);
                // Voices ship with the package and are copied next to the app at build time.
                KokoroVoiceManager.LoadVoicesFromPath(Path.Combine(AppContext.BaseDirectory, "voices"));
                _model = await Task.Run(() => new KokoroModel(path), cancellationToken).ConfigureAwait(false);
                LogLoaded();
            }

            return _model;
        }
        finally
        {
            _engine.Release();
        }
    }

    private async Task<string> EnsureModelAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_options.ModelDirectory, "kokoro.onnx");
        if (File.Exists(path))
        {
            return path;
        }

        LogDownloading(path);
        Directory.CreateDirectory(_options.ModelDirectory);
        var temporary = path + ".download";
        await using (var source = await _http.GetStreamAsync(new Uri(ModelUrl), cancellationToken).ConfigureAwait(false))
        await using (var target = File.Create(temporary))
        {
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static byte[] ToPcm16(float[] samples)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue);
        }

        return MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading Kokoro model to {Path}")]
    private partial void LogDownloading(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kokoro model loaded")]
    private partial void LogLoaded();
}

public sealed class KokoroOptions
{
    public const string SectionName = "TextToSpeech:Kokoro";

    /// <summary>Where the model is stored; the app sets this to its data folder.</summary>
    public string ModelDirectory { get; set; } = "models";
}
