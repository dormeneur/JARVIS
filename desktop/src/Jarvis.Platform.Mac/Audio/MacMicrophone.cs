using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Jarvis.Contracts.Audio;
using Jarvis.Platform.Audio;
using Microsoft.Extensions.Options;

namespace Jarvis.Platform.Mac.Audio;

/// <summary>
/// The chosen (or default) input through AVAudioEngine, converted natively to the engine's PCM16
/// format. A microphone blocked in System Settings → Privacy throws <see cref="MicrophoneBlockedException"/>.
/// </summary>
public sealed unsafe class MacMicrophone : IMicrophone
{
    /// <summary>Where macOS lets the user allow the microphone.</summary>
    public const string SettingsPage = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";

    private readonly IOptionsMonitor<MicrophoneOptions> _options;

    public MacMicrophone(IOptionsMonitor<MicrophoneOptions> options)
    {
        _options = options;
    }

    public string DeviceName
    {
        get
        {
            var devices = Devices();
            var id = _options.CurrentValue.DeviceId;
            return devices.FirstOrDefault(device => device.Id == id).Name
                   ?? devices.FirstOrDefault(device => device.IsDefault).Name
                   ?? "No microphone found";
        }
    }

    public static bool BlockedByMacOS() => Native.MicAccess(0) == 1;

    public static IReadOnlyList<(string Id, string Name, bool IsDefault)> Devices()
    {
        using var document = JsonDocument.Parse(Native.Take(Native.MicDevices()) ?? "[]");
        return document.RootElement.EnumerateArray()
            .Select(device => (device.GetProperty("id").GetString() ?? string.Empty, device.GetProperty("name").GetString() ?? "Microphone", device.GetProperty("default").GetBoolean()))
            .ToList();
    }

    public IMicrophoneSession Start(AudioFormat format)
    {
        var id = _options.CurrentValue.DeviceId;
        // An unplugged choice falls back to the default, as on Windows.
        var uid = !string.IsNullOrEmpty(id) && Devices().Any(device => device.Id == id) ? id : null;
        return new Session(format, uid);
    }

    private sealed class Session : IMicrophoneSession
    {
        private readonly Channel<ReadOnlyMemory<byte>> _audio = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly GCHandle _self;
        private nint _handle;

        public Session(AudioFormat format, string? uid)
        {
            _self = GCHandle.Alloc(this);
            _handle = Native.MicStart(format.SampleRate, format.Channels, uid, &OnChunk, GCHandle.ToIntPtr(_self), out var error);
            if (_handle == 0)
            {
                _self.Free();
                throw error == 1
                    ? new MicrophoneBlockedException("macOS is blocking Jarvis from using the microphone.")
                    : new InvalidOperationException("No microphone is available.");
            }
        }

        public ChannelReader<ReadOnlyMemory<byte>> Audio => _audio.Reader;

        public ValueTask DisposeAsync()
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0)
            {
                Native.MicStop(handle); // removes the tap: no chunk arrives after this returns
                _self.Free();
                _audio.Writer.TryComplete();
            }

            return ValueTask.CompletedTask;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static void OnChunk(nint context, byte* bytes, int count)
        {
            if (GCHandle.FromIntPtr(context).Target is Session session && count > 0)
            {
                session._audio.Writer.TryWrite(new ReadOnlySpan<byte>(bytes, count).ToArray());
            }
        }
    }
}
