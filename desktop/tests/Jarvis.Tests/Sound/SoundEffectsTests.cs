using System.Runtime.InteropServices;
using Jarvis.Contracts.Audio;
using Jarvis.Core.Sound;

namespace Jarvis.Tests.Sound;

public sealed class SoundEffectsTests
{
    [Theory]
    [InlineData(SoundEffect.ComposerOpen, 0.35)]
    [InlineData(SoundEffect.JobDone, 0.70)]
    public void Render_IsShort_AudibleAtItsVolume_AndEndsInSilence(SoundEffect effect, double volume)
    {
        var samples = MemoryMarshal.Cast<byte, short>(SoundEffects.Render(effect));
        var peak = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        Assert.InRange(samples.Length / 48_000.0, 0.1, 1.0);
        Assert.InRange(peak / (double)short.MaxValue, volume * 0.3, volume * 0.6);
        Assert.InRange(Math.Abs((int)samples[^1]), 0, 50);
    }

    [Fact]
    public void EveryEffect_Renders()
    {
        foreach (var effect in Enum.GetValues<SoundEffect>())
        {
            Assert.NotEmpty(SoundEffects.Render(effect));
        }
    }

    [Fact]
    public void Variants_DifferButEachIsStable()
    {
        Assert.NotEqual(SoundEffects.Render(SoundEffect.JobDone, 0), SoundEffects.Render(SoundEffect.JobDone, 1));
        Assert.Equal(SoundEffects.Render(SoundEffect.Drawing, 2), SoundEffects.Render(SoundEffect.Drawing, 2));
    }
}
