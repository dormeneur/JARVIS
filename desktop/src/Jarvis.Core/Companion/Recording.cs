using Jarvis.Contracts.Audio;
using Jarvis.Contracts.Speech;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Companion;

/// <summary>Microphone audio flowing into a transcription session while a key is held.</summary>
internal sealed partial class Recording : IAsyncDisposable
{
    private readonly IMicrophoneSession _microphone;
    private readonly Task<ITranscriptionSession> _transcription;
    private readonly Task _pump;
    private int _disposed;

    private Recording(IMicrophoneSession microphone, Task<ITranscriptionSession> transcription, Action<double> level, ILogger logger)
    {
        _microphone = microphone;
        _transcription = transcription;
        _pump = PumpAsync(level, logger);
    }

    /// <param name="level">Called with each chunk's 0–1 loudness, for a meter.</param>
    public static Recording Start(IMicrophone microphone, ISpeechToText speechToText, TranscriptionRequest request, Action<double> level, ILogger logger)
    {
        // The microphone starts first and buffers, so nothing is lost while the engine spins up.
        var session = microphone.Start(speechToText.InputFormat);
        return new Recording(session, speechToText.StartSessionAsync(request, CancellationToken.None), level, logger);
    }

    /// <summary>Stops the microphone and returns everything that was said.</summary>
    public async Task<string> FinishAsync(CancellationToken cancellationToken)
    {
        await _microphone.DisposeAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        var transcription = await _transcription.ConfigureAwait(false);
        return await transcription.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _microphone.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
            await (await _transcription.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Already reported by the pump or the turn; disposal must not throw.
        }
    }

    private async Task PumpAsync(Action<double> level, ILogger logger)
    {
        try
        {
            var transcription = await _transcription.ConfigureAwait(false);
            await foreach (var chunk in _microphone.Audio.ReadAllAsync().ConfigureAwait(false))
            {
                level(AudioLevel.Of(chunk.Span));
                await transcription.WriteAsync(chunk, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogPumpFailed(logger, exception);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording failed")]
    private static partial void LogPumpFailed(ILogger logger, Exception exception);
}
