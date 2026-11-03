namespace Jarvis.Contracts.Audio;

/// <summary>Plays PCM16 on the default output device.</summary>
public interface IAudioPlayer
{
    /// <summary>
    /// Plays the stream as it arrives and completes once the last sample has been heard.
    /// Cancelling stops playback at once.
    /// </summary>
    Task PlayAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcm16, AudioFormat format, CancellationToken cancellationToken);
}
