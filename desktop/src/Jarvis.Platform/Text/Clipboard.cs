using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace Jarvis.Platform.Text;

/// <summary>
/// Just enough Win32 clipboard to borrow it: save everything that's on it, put text on it, put the
/// saved content back. Text we place is kept out of clipboard history (Win+V) and cloud sync.
/// </summary>
internal static class Clipboard
{
    private const uint UnicodeText = 13;
    private const int OpenAttempts = 10;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Formats that hold GDI handles rather than memory; they can't be copied byte-for-byte.</summary>
    private static readonly HashSet<uint> HandleFormats = [2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E];

    private static readonly uint ExcludeFromHistory = PInvoke.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

    public static uint SequenceNumber => PInvoke.GetClipboardSequenceNumber();

    /// <summary>Copies every memory-backed format currently on the clipboard.</summary>
    public static unsafe List<(uint Format, byte[] Data)> Snapshot()
    {
        var saved = new List<(uint, byte[])>();
        using var _ = Open();
        for (var format = PInvoke.EnumClipboardFormats(0); format != 0; format = PInvoke.EnumClipboardFormats(format))
        {
            if (HandleFormats.Contains(format))
            {
                continue;
            }

            var handle = PInvoke.GetClipboardData(format);
            if (handle.IsNull)
            {
                continue;
            }

            var memory = new HGLOBAL(handle.Value);
            var size = (int)PInvoke.GlobalSize(memory);
            var pointer = PInvoke.GlobalLock(memory);
            if (pointer is null)
            {
                continue;
            }

            try
            {
                saved.Add((format, new ReadOnlySpan<byte>(pointer, size).ToArray()));
            }
            finally
            {
                PInvoke.GlobalUnlock(memory);
            }
        }

        return saved;
    }

    public static void Restore(List<(uint Format, byte[] Data)> saved)
    {
        using var _ = Open();
        PInvoke.EmptyClipboard();
        foreach (var (format, data) in saved)
        {
            Put(format, data);
        }
    }

    /// <summary>Replaces the clipboard with <paramref name="text"/>, hidden from clipboard history.</summary>
    public static void SetText(string text)
    {
        using var _ = Open();
        PInvoke.EmptyClipboard();
        // Windows text uses CRLF; a bare LF is dropped or ignored by many apps (Notepad included).
        var windowsText = text.ReplaceLineEndings("\r\n");
        Put(UnicodeText, MemoryMarshal.AsBytes((windowsText + '\0').AsSpan()).ToArray());
        Put(ExcludeFromHistory, [0, 0, 0, 0]);
    }

    private static unsafe void Put(uint format, byte[] data)
    {
        var memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
        if (memory.IsNull)
        {
            return;
        }

        var pointer = PInvoke.GlobalLock(memory);
        data.CopyTo(new Span<byte>(pointer, data.Length));
        PInvoke.GlobalUnlock(memory);

        // On success the clipboard owns the memory; on failure it's still ours to free.
        if (PInvoke.SetClipboardData(format, new HANDLE(memory.Value)).IsNull)
        {
            PInvoke.GlobalFree(memory);
        }
    }

    /// <summary>Another app may hold the clipboard for a moment; retry briefly.</summary>
    private static Opened Open()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (PInvoke.OpenClipboard(HWND.Null))
            {
                return default;
            }

            Thread.Sleep(OpenRetryDelay);
        }

        throw new InvalidOperationException("The clipboard is busy in another app.");
    }

    private readonly struct Opened : IDisposable
    {
        public void Dispose() => PInvoke.CloseClipboard();
    }
}
