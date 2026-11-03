using Jarvis.Contracts.Audio;

namespace Jarvis.Contracts.Speech;

/// <summary>
/// Turns spoken audio into text. One session per push-to-talk press or dictation hold.
/// Implementations: local Whisper (free default); a streaming cloud engine can be added later.
/// </summary>
public interface ISpeechToText
{
    /// <summary>The PCM format <see cref="ITranscriptionSession.WriteAsync"/> expects.</summary>
    AudioFormat InputFormat { get; }

    /// <summary>
    /// Opens a session. Callers should start it when the key goes down, not when it is released,
    /// so engines that stream can begin work immediately.
    /// </summary>
    Task<ITranscriptionSession> StartSessionAsync(TranscriptionRequest request, CancellationToken cancellationToken);

    /// <summary>Downloads and loads models ahead of the first utterance. Optional.</summary>
    Task PrewarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <param name="Language">BCP-47 code, or <see langword="null"/> to auto-detect.</param>
/// <param name="Vocabulary">Words the engine should favour (names, jargon). May be empty.</param>
public sealed record TranscriptionRequest(string? Language, IReadOnlyList<string> Vocabulary)
{
    public static TranscriptionRequest AutoDetect { get; } = new(null, []);
}

/// <summary>One utterance. Dispose without completing to abandon it.</summary>
public interface ITranscriptionSession : IAsyncDisposable
{
    /// <summary>Appends PCM16 audio in <see cref="ISpeechToText.InputFormat"/>.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken);

    /// <summary>Signals end of speech and returns the final transcript (empty if nothing was said).</summary>
    Task<string> CompleteAsync(CancellationToken cancellationToken);
}
