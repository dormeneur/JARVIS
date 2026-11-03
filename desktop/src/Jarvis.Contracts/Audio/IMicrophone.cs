using System.Threading.Channels;

namespace Jarvis.Contracts.Audio;

/// <summary>Captures the default input device as PCM16 in the format the caller asks for.</summary>
public interface IMicrophone
{
    /// <summary>The device that will be used, as Windows names it (for settings and diagnostics).</summary>
    string DeviceName { get; }

    /// <summary>Starts capturing immediately. Dispose the session to stop.</summary>
    IMicrophoneSession Start(AudioFormat format);
}

public interface IMicrophoneSession : IAsyncDisposable
{
    /// <summary>PCM16 chunks in the requested format; completes when the session stops or the device fails.</summary>
    ChannelReader<ReadOnlyMemory<byte>> Audio { get; }
}
