using System.Diagnostics;
using Windows.Win32;

namespace Jarvis.Platform.Browser;

/// <summary>Which window is in front, on Windows.</summary>
public sealed partial class BrowserLinkServer
{
    private static readonly string[] Browsers = ["chrome", "msedge", "brave", "arc", "vivaldi", "opera", "chromium"];

    private static partial bool BrowserInFront()
    {
        uint process;
        unsafe
        {
            PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), &process);
        }

        try
        {
            using var owner = Process.GetProcessById((int)process);
            return Browsers.Contains(owner.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static partial string FrontWindowTitle()
    {
        Span<char> buffer = stackalloc char[512];
        int length;
        unsafe
        {
            fixed (char* text = buffer)
            {
                length = PInvoke.GetWindowText(PInvoke.GetForegroundWindow(), text, buffer.Length);
            }
        }

        return new string(buffer[..Math.Max(0, length)]);
    }
}
