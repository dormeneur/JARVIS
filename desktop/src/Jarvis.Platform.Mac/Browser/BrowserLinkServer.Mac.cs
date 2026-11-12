using Jarvis.Platform.Mac.Shell;

namespace Jarvis.Platform.Browser;

/// <summary>Which window is in front, on a Mac (the shared server is linked from Jarvis.Platform).</summary>
public sealed partial class BrowserLinkServer
{
    private static readonly string[] Browsers = ["com.google.Chrome", "com.microsoft.edgemac", "com.brave.Browser", "company.thebrowser.Browser", "com.vivaldi.Vivaldi", "com.operasoftware.Opera", "org.chromium.Chromium"];

    private static partial bool BrowserInFront()
        => MacActivityWatch.Front() is { } front && Browsers.Any(id => front.Bundle.StartsWith(id, StringComparison.OrdinalIgnoreCase));

    private static partial string FrontWindowTitle() => MacActivityWatch.Front()?.Title ?? string.Empty;
}
