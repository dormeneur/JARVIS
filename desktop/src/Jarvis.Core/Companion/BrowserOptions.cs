namespace Jarvis.Core.Companion;

/// <summary>Browser link settings, bound from the "Browser" settings section.</summary>
public sealed class BrowserOptions
{
    public const string SectionName = "Browser";

    /// <summary>Extension IDs the user approved; anything else that connects is turned away.</summary>
    public List<string> AllowedExtensions { get; set; } = [];

    /// <summary>When you ask something with a browser in front, read that tab's text for Claude.</summary>
    public bool ReadPage { get; set; } = true;
}
