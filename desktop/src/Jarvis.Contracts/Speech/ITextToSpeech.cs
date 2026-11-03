using Jarvis.Contracts.Audio;

namespace Jarvis.Contracts.Speech;

/// <summary>
/// Turns text into speech audio. Called once per spoken line so playback of line N overlaps
/// synthesis of line N+1. Implementations: Kokoro (free default), Windows SpeechSynthesizer (fallback).
/// </summary>
public interface ITextToSpeech
{
    AudioFormat OutputFormat { get; }

    /// <summary>Yields PCM16 chunks in <see cref="OutputFormat"/> as soon as they are ready.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, SpeechStyle style, CancellationToken cancellationToken);

    /// <summary>Downloads and loads models ahead of the first reply. Optional.</summary>
    Task PrewarmAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <param name="Voice">Provider-specific voice id; providers fall back to their default if unknown.</param>
/// <param name="Speed">1.0 = normal.</param>
public sealed record SpeechStyle(string Voice, double Speed = 1.0);
