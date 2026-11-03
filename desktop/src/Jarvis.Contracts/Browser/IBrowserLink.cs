namespace Jarvis.Contracts.Browser;

/// <summary>The tab in front of the user, as the browser extension read it.</summary>
public sealed record BrowserPage(string Url, string Title, string Selection, string Text);

/// <summary>The Jarvis Browser Link extension in the user's own browser, once they've paired it.</summary>
public interface IBrowserLink
{
    /// <summary>A paired extension is connected right now.</summary>
    bool IsConnected { get; }

    /// <summary>A browser window is the one the user is working in.</summary>
    bool IsBrowserInFront { get; }

    /// <summary>The active tab, or <see langword="null"/> if the extension didn't answer in time.</summary>
    Task<BrowserPage?> ReadPageAsync(CancellationToken cancellationToken);

    /// <summary>Opens a link in the user's browser; false if the extension couldn't.</summary>
    Task<bool> OpenAsync(string url, CancellationToken cancellationToken);
}
