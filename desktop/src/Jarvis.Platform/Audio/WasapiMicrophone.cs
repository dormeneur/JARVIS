using System.Runtime.InteropServices;
using System.Threading.Channels;
using Jarvis.Contracts.Audio;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.Platform.Audio;

/// <summary>
/// The chosen microphone (or the Windows default) through WASAPI shared mode, converted on the fly
/// (any rate or channel count) to the PCM16 format the speech engine wants.
/// </summary>
public sealed class WasapiMicrophone : IMicrophone
{
    private readonly IOptionsMonitor<MicrophoneOptions> _options;

    public WasapiMicrophone(IOptionsMonitor<MicrophoneOptions> options)
    {
        _options = options;
    }

    public string DeviceName
    {
        get
        {
            try
            {
                using var device = Open();
                return device.FriendlyName;
            }
            catch (COMException)
            {
                return "No microphone found";
            }
        }
    }

    /// <summary>Microphones plugged in right now, as endpoint ID and name.</summary>
    public static IReadOnlyList<(string Id, string Name)> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<(string, string)>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                devices.Add((device.ID, device.FriendlyName));
            }
        }

        return devices;
    }

    public IMicrophoneSession Start(AudioFormat format)
    {
        var device = Open();
        try
        {
            return new Session(device, format);
        }
        catch (UnauthorizedAccessException exception)
        {
            device.Dispose();
            throw new MicrophoneBlockedException("Windows is blocking desktop apps from using the microphone.", exception);
        }
    }

    /// <summary>
    /// Whether Windows' privacy settings block desktop apps (Jarvis among them) from the microphone:
    /// the all-apps switch or "Let desktop apps access your microphone".
    /// </summary>
    public static bool BlockedByWindows()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        static bool Denied(Microsoft.Win32.RegistryKey hive, string key)
        {
            using var settings = hive.OpenSubKey(key);
            return settings?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }

        return Denied(Microsoft.Win32.Registry.LocalMachine, store)
               || Denied(Microsoft.Win32.Registry.CurrentUser, store)
               || Denied(Microsoft.Win32.Registry.CurrentUser, store + @"\NonPackaged");
    }

    /// <summary>The chosen microphone, or the Windows default when none is chosen or it's unplugged.</summary>
    private MMDevice Open()
    {
        using var enumerator = new MMDeviceEnumerator();
        var id = _options.CurrentValue.DeviceId;
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active)
                {
                    return device;
                }

                device.Dispose();
            }
            catch (COMException)
            {
                // Removed since it was chosen.
            }
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    private sealed class Session : IMicrophoneSession
    {
        private readonly MMDevice _device;
        private readonly WasapiCapture _capture;
        private readonly BufferedWaveProvider _input;
        private readonly ISampleProvider _converted;
        private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly float[] _scratch;

        public Session(MMDevice device, AudioFormat format)
        {
            _device = device;
            _capture = new WasapiCapture(device);
            _input = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = false };
            ISampleProvider samples = new DownmixToMono(_input.ToSampleProvider());
            _converted = new WdlResamplingSampleProvider(samples, format.SampleRate);
            _scratch = new float[format.SampleRate / 10];

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += (_, e) =>
            {
                _audio.Writer.TryComplete(e.Exception);
                _stopped.TrySetResult();
            };
            _capture.StartRecording();
        }

        public ChannelReader<ReadOnlyMemory<byte>> Audio => _audio.Reader;

        public async ValueTask DisposeAsync()
        {
            _capture.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            _capture.Dispose();
            _device.Dispose();
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            _input.AddSamples(e.Buffer, 0, e.BytesRecorded);
            int read;
            while ((read = _converted.Read(_scratch, 0, _scratch.Length)) > 0)
            {
                var pcm = new short[read];
                for (var i = 0; i < read; i++)
                {
                    pcm[i] = (short)Math.Clamp(_scratch[i] * 32767f, short.MinValue, short.MaxValue);
                }

                _audio.Writer.TryWrite(MemoryMarshal.AsBytes(pcm.AsSpan()).ToArray());
            }
        }
    }

    /// <summary>Averages any number of channels into one; NAudio only ships a stereo version.</summary>
    private sealed class DownmixToMono(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _buffer = [];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            if (_channels == 1)
            {
                return source.Read(buffer, offset, count);
            }

            var needed = count * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            var frames = source.Read(_buffer, 0, needed) / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _buffer[(frame * _channels) + channel];
                }

                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}
