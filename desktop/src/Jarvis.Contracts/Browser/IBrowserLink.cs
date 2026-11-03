namespace Jarvis.Contracts.Browser;

/// <summary>The tab in front of the user, as the browser extension read it.</summary>
/// <param name="Elements">Its buttons, links and fields, each with an id Jarvis can act on.</param>
/// <param name="Tabs">The tabs in its window.</param>
public sealed record BrowserPage(string Url, string Title, string Selection, string Text, IReadOnlyList<PageElement>? Elements = null, IReadOnlyList<BrowserTab>? Tabs = null);

/// <param name="Id">"e12": what a step names to act on it.</param>
/// <param name="Kind">button, link, field or dropdown.</param>
/// <param name="OnScreen">Visible now; otherwise the page has to scroll to it (acting on it scrolls there).</param>
public sealed record PageElement(string Id, string Kind, string Label, bool OnScreen);

/// <param name="Id">"T123": what a step names to switch to or close it.</param>
public sealed record BrowserTab(string Id, string Title, bool Active);

/// <summary>One approved step for the browser: press, fill, copy, paste, scroll, tab-switch or tab-close.</summary>
/// <param name="Target">An element id ("e12"), a tab id ("T123"), "selection", or empty.</param>
/// <param name="Text">What to type, or for scroll: up, down, top or bottom.</param>
public sealed record BrowserAct(string Action, string Target, string? Text);

/// <param name="Focused">A field the page wouldn't take a scripted value in: focused and selected, ready for real keystrokes.</param>
/// <param name="Text">What a copy step copied.</param>
public sealed record BrowserActResult(bool Done, int? X, int? Y, bool Focused = false, string? Reason = null, string? Text = null);

/// <summary>A web form the extension read off the active tab.</summary>
/// <param name="Description">The form's own description (or the page's), which often names the organisation.</param>
/// <param name="Problem">Why there are no questions, in words for the user (null when there are some).</param>
public sealed record BrowserForm(string Url, string Title, IReadOnlyList<FormQuestion> Questions, string Description = "", string? Problem = null);

/// <summary>How putting an answer into the page went, and where the field is on screen (physical desktop pixels).</summary>
/// <param name="Filled">The page shows the value now (read back, not assumed).</param>
/// <param name="Focused">It's a text field the page wouldn't take a scripted value in; it is focused with its text selected, ready for real keystrokes.</param>
/// <param name="Reason">Why it isn't filled, in a few words.</param>
public sealed record FormFill(bool Filled, int? X, int? Y, bool Focused = false, string? Reason = null);

/// <param name="Id">Stable within one reading of the page.</param>
/// <param name="Kind">text, paragraph, choice, checkbox, dropdown, date, time or other.</param>
/// <param name="Options">The offered choices, for choice, checkbox and dropdown questions.</param>
public sealed record FormQuestion(string Id, string Text, string Kind, IReadOnlyList<string> Options, bool Required);

/// <summary>The Jarvis Browser Link extension in the user's own browser, once they've paired it.</summary>
public interface IBrowserLink
{
    /// <summary>A paired extension is connected right now.</summary>
    bool IsConnected { get; }

    /// <summary>A browser window is the one the user is working in.</summary>
    bool IsBrowserInFront { get; }

    /// <summary>The active tab, or <see langword="null"/> if the extension didn't answer in time.</summary>
    Task<BrowserPage?> ReadPageAsync(CancellationToken cancellationToken);

    /// <summary>The form on the active tab; with no questions, <see cref="BrowserForm.Problem"/> says why. Null only when no extension is linked.</summary>
    Task<BrowserForm?> ReadFormAsync(CancellationToken cancellationToken) => Task.FromResult<BrowserForm?>(null);

    /// <summary>
    /// Puts <paramref name="value"/> into the question <paramref name="questionId"/> of the form last
    /// read (typing it, or clicking the matching option), scrolled into view. Never submits.
    /// </summary>
    Task<FormFill?> FillFormAsync(string questionId, string value, CancellationToken cancellationToken) => Task.FromResult<FormFill?>(null);

    /// <summary>Whether the question in the page shows <paramref name="value"/> now.</summary>
    Task<bool> CheckFormAsync(string questionId, string value, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Does one approved step in the browser; <see langword="null"/> if the extension didn't answer.</summary>
    Task<BrowserActResult?> ActAsync(BrowserAct act, CancellationToken cancellationToken) => Task.FromResult<BrowserActResult?>(null);

    /// <summary>Opens a link in the user's browser; false if the extension couldn't.</summary>
    Task<bool> OpenAsync(string url, CancellationToken cancellationToken);
}
