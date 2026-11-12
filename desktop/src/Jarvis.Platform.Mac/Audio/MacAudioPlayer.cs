using Jarvis.Contracts.Audio;

namespace Jarvis.Platform.Mac.Audio;

/// <summary>
/// Plays PCM16 to the default output through AVAudioEngine (which resamples to the device), and
/// returns once the last buffer has really been heard. Cancelling stops at once.
/// </summary>
public sealed class MacAudioPlayer : IAudioPlayer
{
    private static readonly TimeSpan DrainPoll = TimeSpan.FromMilliseconds(20);

    public async Task PlayAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcm16, AudioFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        var player = Native.PlayerOpen(format.SampleRate, format.Channels);
        if (player == 0)
        {
            return; // no output device: nothing to hear
        }

        try
        {
            await foreach (var chunk in pcm16.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                Write(player, chunk.Span);
            }

            while (Native.PlayerQueued(player) > 0)
            {
                await Task.Delay(DrainPoll, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Native.PlayerClose(player);
        }
    }

    private static unsafe void Write(nint player, ReadOnlySpan<byte> chunk)
    {
        fixed (byte* bytes = chunk)
        {
            Native.PlayerWrite(player, bytes, chunk.Length);
        }
    }
}
