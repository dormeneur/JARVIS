using Jarvis.Contracts.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.Platform.Audio;

/// <summary>
/// Streams PCM16 to the default speakers through WASAPI shared mode, converting to the device's
/// mix format so any engine's sample rate plays correctly.
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private const int LatencyMilliseconds = 60;
    private static readonly TimeSpan DrainPoll = TimeSpan.FromMilliseconds(20);

    public async Task PlayAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> pcm16, AudioFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        var source = new BufferedWaveProvider(new WaveFormat(format.SampleRate, 16, format.Channels))
        {
            BufferDuration = TimeSpan.FromMinutes(10),
            ReadFully = true,
        };

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var mix = device.AudioClient.MixFormat;
        ISampleProvider samples = new WdlResamplingSampleProvider(source.ToSampleProvider(), mix.SampleRate);
        if (format.Channels == 1 && mix.Channels == 2)
        {
            samples = new MonoToStereoSampleProvider(samples);
        }

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMilliseconds);
        output.Init(samples.ToWaveProvider());
        output.Play();
        try
        {
            await foreach (var chunk in pcm16.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                source.AddSamples(chunk.ToArray(), 0, chunk.Length);
            }

            // Everything is queued; wait until the device has actually played it.
            while (source.BufferedBytes > 0)
            {
                await Task.Delay(DrainPoll, cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(LatencyMilliseconds * 2), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            output.Stop();
        }
    }
}
