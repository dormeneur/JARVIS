namespace Jarvis.Contracts.Audio;

/// <summary>
/// Shape of a 16-bit little-endian PCM stream. All audio crossing a contract boundary is PCM16 LE;
/// only the rate and channel count vary, so providers can be swapped without format negotiation.
/// </summary>
public readonly record struct AudioFormat
{
    public AudioFormat(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BytesPerSecond => SampleRate * Channels * sizeof(short);

    /// <summary>What speech recognisers (Whisper, Deepgram) consume natively.</summary>
    public static AudioFormat SpeechMono16K { get; } = new(16_000, 1);
}
