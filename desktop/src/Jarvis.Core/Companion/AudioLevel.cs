using System.Runtime.InteropServices;

namespace Jarvis.Core.Companion;

public static class AudioLevel
{
    /// <summary>0–1 loudness of PCM16: RMS on a square-root curve, so normal speech fills a meter.</summary>
    public static double Of(ReadOnlySpan<byte> pcm16)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return Math.Min(1, Math.Sqrt(Math.Sqrt(sum / samples.Length) / short.MaxValue) * 1.6);
    }
}
