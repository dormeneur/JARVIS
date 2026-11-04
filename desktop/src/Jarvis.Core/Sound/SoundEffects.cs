using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jarvis.Contracts.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Sound;

/// <summary>
/// Our own earcons, synthesised to soothe rather than ping: round, low notes (a sine with one faint
/// overtone), eased in over 20 ms (no strike) and left to ring out slowly, in calm intervals in the
/// fourth and fifth octaves. Volumes sit low (DESIGN-FACTS.md §7): feedback 0.2–0.3, news that wants
/// attention 0.5. Each effect has three variants (slightly different pitch and pen strokes), used in
/// turn, so repeats don't sound mechanical.
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
        const double attack = 0.02;
        foreach (var (frequency, start, duration) in notes)
        {
            var first = (int)(start * SampleRate);
            var decay = duration / 2.5;
            for (var i = first; i < Math.Min(length, first + (int)(duration * SampleRate)); i++)
            {
                var t = (double)(i - first) / SampleRate;
                // A raised-cosine swell instead of a strike, then a long, gentle ring-out.
                var swell = t < attack ? 0.5 - (0.5 * Math.Cos(Math.PI * t / attack)) : 1;
                var envelope = swell * Math.Exp(-Math.Max(0, t - attack) / decay);
                var phase = 2 * Math.PI * frequency * t;
                mix[i] += envelope * (Math.Sin(phase) + (0.1 * Math.Sin(2 * phase))) / 1.1;
            }
        }

        // Overlapping notes add up: scale so the loudest moment is exactly the effect's level.
        var loudest = Math.Max(1e-9, mix.Max(Math.Abs));
        var pcm = new short[length];
        for (var i = 0; i < length; i++)
        {
            var fadeOut = Math.Min(1, (length - i) / (0.01 * SampleRate)); // no click at the end
            pcm[i] = (short)Math.Clamp(mix[i] / loudest * volume * fadeOut * 0.6 * short.MaxValue, short.MinValue, short.MaxValue);
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
        SoundEffect.ComposerOpen => (0.25, [(392.00, 0, 0.30), (523.25, 0.08, 0.40)]), // G4 up to C5
        SoundEffect.ComposerSend => (0.28, [(440.00, 0, 0.26), (659.25, 0.07, 0.40)]), // A4 up to E5
        SoundEffect.ComposerClose => (0.25, [(523.25, 0, 0.30), (392.00, 0.08, 0.40)]), // C5 down to G4
        SoundEffect.JobStarted => (0.30, [(261.63, 0, 0.30), (329.63, 0.09, 0.30), (392.00, 0.18, 0.50)]), // C4-E4-G4, unhurried
        SoundEffect.JobDone => (0.50, [(392.00, 0, 0.40), (587.33, 0.12, 0.80)]), // G4 then a long D5
        SoundEffect.JobFailed => (0.45, [(329.63, 0, 0.36), (261.63, 0.14, 0.60)]), // E4 down to C4
        SoundEffect.AnswerReady => (0.22, [(523.25, 0, 0.24), (659.25, 0.06, 0.34)]), // C5 to E5, light
        SoundEffect.ListenStart => (0.20, [(587.33, 0, 0.18), (783.99, 0.05, 0.24)]), // D5 up to G5, a breath
        SoundEffect.ListenEnd => (0.20, [(783.99, 0, 0.18), (587.33, 0.05, 0.26)]), // G5 down to D5
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
