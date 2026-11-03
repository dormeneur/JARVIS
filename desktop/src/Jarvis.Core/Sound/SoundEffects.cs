using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jarvis.Contracts.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Sound;

/// <summary>
/// Our own earcons, synthesised: soft bell-like notes (a sine with two quiet overtones, a 5 ms attack
/// and an exponential decay). Volumes follow the reference app's levels (DESIGN-FACTS.md §7):
/// feedback 0.35–0.55, news that wants attention 0.7. Each effect has three variants (slightly
/// different pitch and pen strokes), used in turn, so repeats don't sound mechanical.
/// </summary>
public sealed partial class SoundEffects : ISoundEffects
{
    private const int SampleRate = 48_000;
    private static readonly AudioFormat Format = new(SampleRate, 1);

    private readonly IAudioPlayer _player;
    private readonly IOptionsMonitor<SoundOptions> _options;
    private readonly ILogger<SoundEffects> _logger;
    private const int Variants = 3;

    private readonly ConcurrentDictionary<(SoundEffect, int), byte[]> _rendered = new();
    private int _played;

    public SoundEffects(IAudioPlayer player, IOptionsMonitor<SoundOptions> options, ILogger<SoundEffects> logger)
    {
        _player = player;
        _options = options;
        _logger = logger;
    }

    public void Play(SoundEffect effect)
    {
        var options = _options.CurrentValue;
        if (options.Enabled && (effect != SoundEffect.Drawing || options.DrawingSounds))
        {
            var variant = (Interlocked.Increment(ref _played) & int.MaxValue) % Variants;
            _ = PlayAsync(_rendered.GetOrAdd((effect, variant), key => Render(key.Item1, key.Item2)));
        }
    }

    /// <summary>The effect as PCM16 mono at 48 kHz.</summary>
    public static byte[] Render(SoundEffect effect, int variant = 0)
    {
        if (effect == SoundEffect.Drawing)
        {
            return PenScratch(variant);
        }

        var pitch = variant switch { 1 => 1.03, 2 => 0.97, _ => 1.0 };
        var (volume, score) = Score(effect);
        var notes = score.Select(note => (note.Frequency * pitch, note.Start, note.Length)).ToArray();
        var length = (int)(notes.Max(note => note.Start + note.Length) * SampleRate);
        var mix = new double[length];
        foreach (var (frequency, start, duration) in notes)
        {
            var first = (int)(start * SampleRate);
            var decay = duration / 3;
            for (var i = first; i < Math.Min(length, first + (int)(duration * SampleRate)); i++)
            {
                var t = (double)(i - first) / SampleRate;
                var envelope = Math.Min(1, t / 0.005) * Math.Exp(-t / decay);
                var phase = 2 * Math.PI * frequency * t;
                mix[i] += envelope * (Math.Sin(phase) + (0.25 * Math.Sin(2 * phase)) + (0.08 * Math.Sin(3 * phase))) / 1.33;
            }
        }

        var pcm = new short[length];
        for (var i = 0; i < length; i++)
        {
            var fadeOut = Math.Min(1, (length - i) / (0.01 * SampleRate)); // no click at the end
            pcm[i] = (short)Math.Clamp(mix[i] * volume * fadeOut * 0.6 * short.MaxValue, short.MinValue, short.MaxValue);
        }

        return MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
    }

    /// <summary>
    /// Three quick strokes of a felt pen: band-passed noise whose loudness rises and falls with each
    /// stroke and flutters with the nib. Seeded, so each variant always sounds the same.
    /// </summary>
    private static byte[] PenScratch(int variant)
    {
        const double volume = 0.25;
        var random = new Random(41 + variant);
        var strokes = new[] { (0.0, 0.32), (0.42, 0.28), (0.8, 0.38) };
        var length = (int)(1.25 * SampleRate);
        var pcm = new short[length];
        double low = 0, lower = 0;
        for (var i = 0; i < length; i++)
        {
            var t = (double)i / SampleRate;
            var envelope = 0.0;
            foreach (var (start, duration) in strokes)
            {
                if (t >= start && t < start + duration)
                {
                    envelope = Math.Sin(Math.PI * (t - start) / duration);
                }
            }

            // Two one-pole low-passes, subtracted: a band of hiss around 1–4 kHz.
            var noise = (random.NextDouble() * 2) - 1;
            low += 0.35 * (noise - low);
            lower += 0.06 * (noise - lower);
            var flutter = 0.75 + (0.25 * Math.Sin(2 * Math.PI * (17 + variant) * t));
            pcm[i] = (short)Math.Clamp((low - lower) * envelope * flutter * volume * 1.6 * short.MaxValue, short.MinValue, short.MaxValue);
        }

        return MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray();
    }

    /// <summary>Volume and notes (Hz, start s, length s) for each effect.</summary>
    private static (double Volume, (double Frequency, double Start, double Length)[] Notes) Score(SoundEffect effect) => effect switch
    {
        SoundEffect.ComposerOpen => (0.35, [(659.25, 0, 0.12), (880.00, 0.06, 0.16)]), // E5 up to A5
        SoundEffect.ComposerSend => (0.50, [(880.00, 0, 0.09), (1318.51, 0.045, 0.14)]), // A5 up to E6, quick
        SoundEffect.ComposerClose => (0.50, [(880.00, 0, 0.10), (659.25, 0.06, 0.16)]), // A5 down to E5
        SoundEffect.JobStarted => (0.55, [(523.25, 0, 0.14), (659.25, 0.07, 0.14), (783.99, 0.14, 0.22)]), // C-E-G
        SoundEffect.JobDone => (0.70, [(783.99, 0, 0.20), (1174.66, 0.09, 0.45)]), // G5 then a ringing D6
        SoundEffect.JobFailed => (0.70, [(659.25, 0, 0.18), (523.25, 0.12, 0.30)]), // E5 down to C5
        SoundEffect.AnswerReady => (0.35, [(1046.50, 0, 0.10), (1318.51, 0.05, 0.18)]), // C6 to E6, light
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unknown sound effect."),
    };

    private async Task PlayAsync(byte[] pcm)
    {
        try
        {
            await _player.PlayAsync(Once(pcm), Format, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LogPlayFailed(exception); // a missing sound must never break what it decorates
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Once(byte[] pcm, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return pcm;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not play a sound effect")]
    private partial void LogPlayFailed(Exception exception);
}
