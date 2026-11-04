using System.Text.RegularExpressions;

namespace Jarvis.Core.Assistant;

/// <summary>An app in the Start menu's list: its name and how Windows launches it (its AppsFolder id).</summary>
public sealed record InstalledApp(string Name, string LaunchId);

/// <summary>The PC's installed apps, and opening things without looking at the screen.</summary>
public interface IApps
{
    /// <summary>Every app Windows lists (Start menu desktop apps and Store apps alike).</summary>
    IReadOnlyList<InstalledApp> All { get; }

    bool Launch(InstalledApp app);

    /// <summary>Opens a link or deep link (https:, ms-settings:, spotify:, mailto:, shell:…) with its app.</summary>
    bool Open(string uri);
}

/// <summary>A one-tap next step shown after something opens.</summary>
public sealed record Suggestion(string Label, string Uri);

/// <summary>What a local action did, in words, with next steps.</summary>
public sealed record ActionDone(string Said, IReadOnlyList<Suggestion> Next);

/// <summary>
/// Requests the PC can do itself, instantly, without Claude or a screenshot: open an app, a
/// Settings page, a website or a deep link, or search the web, YouTube or Spotify. Anything it
/// doesn't recognise returns null and goes to the brain as before, so the saving costs nothing.
/// </summary>
public sealed partial class LocalActions(IApps apps)
{
    /// <summary>Takes a suggested next step (a deep link).</summary>
    public bool Follow(Suggestion next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return apps.Open(next.Uri);
    }

    /// <summary>Does it, or returns null when the request isn't one of these.</summary>
    public ActionDone? TryHandle(string request)
    {
        var text = Polite().Replace(request.Trim().TrimEnd('.', '!', '?'), string.Empty).Trim();
        if (Search().Match(text) is { Success: true } search)
        {
            return SearchFor(search.Groups["what"].Value.Trim(), search.Groups["where"].Value.Trim().ToLowerInvariant());
        }

        if (Play().Match(text) is { Success: true } play && apps.Open($"spotify:search:{Uri.EscapeDataString(play.Groups["what"].Value.Trim())}"))
        {
            return new ActionDone($"Searching Spotify for {play.Groups["what"].Value.Trim()}.", SuggestionsFor("spotify"));
        }

        return Open().Match(text) is { Success: true } open ? OpenThing(open.Groups["what"].Value.Trim()) : null;
    }

    private ActionDone? OpenThing(string what)
    {
        var name = Filler().Replace(what, " ").Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var key = Normalize(name);
        if (SettingsPages.TryGetValue(key.Replace(" settings", string.Empty, StringComparison.Ordinal), out var page) && (key.EndsWith("settings", StringComparison.Ordinal) || key is "bluetooth" or "wifi" or "wi fi"))
        {
            return apps.Open($"ms-settings:{page}") ? new ActionDone($"Opened {name.Replace(" settings", string.Empty, StringComparison.OrdinalIgnoreCase)} settings.", []) : null;
        }

        if (Folders.TryGetValue(key, out var folder) && apps.Open(folder))
        {
            return new ActionDone($"Opened {name}.", []);
        }

        if (Find(name) is { } app && apps.Launch(app))
        {
            return new ActionDone($"Opened {app.Name}.", SuggestionsFor(app.Name));
        }

        if (Sites.TryGetValue(key, out var site) || (LooksLikeAddress().IsMatch(name) && (site = "https://" + name.ToLowerInvariant()) is not null))
        {
            return apps.Open(site) ? new ActionDone($"Opened {name}.", SuggestionsFor(key)) : null;
        }

        return null; // not something the PC knows by itself: the brain takes it
    }

    private ActionDone? SearchFor(string what, string where)
    {
        if (what.Length == 0)
        {
            return null;
        }

        var query = Uri.EscapeDataString(what);
        var (uri, place) = where switch
        {
            "youtube" => ($"https://www.youtube.com/results?search_query={query}", "YouTube"),
            "spotify" => ($"spotify:search:{query}", "Spotify"),
            "github" => ($"https://github.com/search?q={query}", "GitHub"),
            "amazon" => ($"https://www.amazon.in/s?k={query}", "Amazon"),
            "maps" or "google maps" => ($"https://www.google.com/maps/search/{query}", "Google Maps"),
            "wikipedia" => ($"https://en.wikipedia.org/w/index.php?search={query}", "Wikipedia"),
            _ => ($"https://www.google.com/search?q={query}", "the web"),
        };
        return apps.Open(uri) ? new ActionDone($"Searching {place} for {what}.", []) : null;
    }

    /// <summary>
    /// The installed app a spoken or typed name means: an alias first ("vs code"), then the name
    /// itself, then one that starts with it, then one whose words include all of it.
    /// </summary>
    public InstalledApp? Find(string name)
    {
        var key = Normalize(name);
        if (Aliases.TryGetValue(key, out var alias))
        {
            key = Normalize(alias);
        }

        var all = apps.All;
        return all.FirstOrDefault(app => Normalize(app.Name) == key)
            ?? all.Where(app => Normalize(app.Name).StartsWith(key + " ", StringComparison.Ordinal)).MinBy(app => app.Name.Length)
            ?? all.Where(app => key.Length >= 3 && Normalize(app.Name).Split(' ').Contains(key)).MinBy(app => app.Name.Length)
            ?? all.Where(app => key.Length >= 4 && Normalize(app.Name).Contains(key, StringComparison.Ordinal)).MinBy(app => app.Name.Length);
    }

    /// <summary>Next steps for an app or site, as deep links (no brain needed); none for most.</summary>
    public static IReadOnlyList<Suggestion> SuggestionsFor(string appOrSite)
    {
        var key = Normalize(appOrSite);
        return Next.FirstOrDefault(pair => key.Contains(pair.Key, StringComparison.Ordinal)).Value ?? [];
    }

    private static string Normalize(string text) => Spaces().Replace(NotWord().Replace(text.ToLowerInvariant(), " "), " ").Trim();

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["vs code"] = "Visual Studio Code", ["vscode"] = "Visual Studio Code", ["code"] = "Visual Studio Code",
        ["word"] = "Word", ["excel"] = "Excel", ["powerpoint"] = "PowerPoint", ["outlook"] = "Outlook",
        ["teams"] = "Microsoft Teams", ["edge"] = "Microsoft Edge", ["chrome"] = "Google Chrome",
        ["calculator"] = "Calculator", ["calc"] = "Calculator", ["notepad"] = "Notepad", ["paint"] = "Paint",
        ["terminal"] = "Terminal", ["cmd"] = "Command Prompt", ["command prompt"] = "Command Prompt",
        ["task manager"] = "Task Manager", ["camera"] = "Camera", ["photos"] = "Photos", ["clock"] = "Clock",
        ["store"] = "Microsoft Store", ["microsoft store"] = "Microsoft Store",
    };

    /// <summary>Settings pages by what people call them (ms-settings: page names).</summary>
    private static readonly Dictionary<string, string> SettingsPages = new()
    {
        ["bluetooth"] = "bluetooth", ["wifi"] = "network-wifi", ["wi fi"] = "network-wifi", ["network"] = "network",
        ["display"] = "display", ["sound"] = "sound", ["audio"] = "sound", ["battery"] = "batterysaver", ["power"] = "powersleep",
        ["notifications"] = "notifications", ["apps"] = "appsfeatures", ["default apps"] = "defaultapps", ["storage"] = "storagesense",
        ["windows update"] = "windowsupdate", ["update"] = "windowsupdate", ["privacy"] = "privacy", ["microphone"] = "privacy-microphone",
        ["camera"] = "privacy-webcam", ["mouse"] = "mousetouchpad", ["keyboard"] = "keyboard", ["language"] = "regionlanguage",
        ["date and time"] = "dateandtime", ["time"] = "dateandtime", ["personalization"] = "personalization", ["background"] = "personalization-background",
        ["themes"] = "themes", ["accounts"] = "yourinfo", ["vpn"] = "network-vpn", ["printers"] = "printers", ["focus"] = "quiethours",
    };

    private static readonly Dictionary<string, string> Folders = new()
    {
        ["downloads"] = "shell:Downloads", ["documents"] = "shell:Personal", ["desktop"] = "shell:Desktop", ["pictures"] = "shell:My Pictures",
        ["music"] = "shell:My Music", ["videos"] = "shell:My Video", ["file explorer"] = "shell:MyComputerFolder", ["explorer"] = "shell:MyComputerFolder",
        ["recycle bin"] = "shell:RecycleBinFolder", ["this pc"] = "shell:MyComputerFolder", ["startup"] = "shell:Startup",
    };

    private static readonly Dictionary<string, string> Sites = new()
    {
        ["youtube"] = "https://www.youtube.com", ["gmail"] = "https://mail.google.com", ["google"] = "https://www.google.com",
        ["github"] = "https://github.com", ["google calendar"] = "https://calendar.google.com", ["google drive"] = "https://drive.google.com",
        ["drive"] = "https://drive.google.com", ["linkedin"] = "https://www.linkedin.com", ["chatgpt"] = "https://chatgpt.com", ["claude"] = "https://claude.ai",
        ["netflix"] = "https://www.netflix.com", ["amazon"] = "https://www.amazon.in", ["twitter"] = "https://x.com", ["x"] = "https://x.com",
        ["reddit"] = "https://www.reddit.com", ["instagram"] = "https://www.instagram.com", ["maps"] = "https://www.google.com/maps",
        ["google maps"] = "https://www.google.com/maps", ["whatsapp web"] = "https://web.whatsapp.com", ["notion"] = "https://www.notion.so",
    };

    private static readonly Dictionary<string, IReadOnlyList<Suggestion>> Next = new()
    {
        ["spotify"] = [new("Liked songs", "spotify:collection:tracks"), new("Your library", "spotify:collection"), new("Search", "spotify:search")],
        ["chrome"] = [new("Gmail", "https://mail.google.com"), new("YouTube", "https://www.youtube.com"), new("Calendar", "https://calendar.google.com")],
        ["edge"] = [new("Gmail", "https://mail.google.com"), new("YouTube", "https://www.youtube.com"), new("Calendar", "https://calendar.google.com")],
        ["brave"] = [new("Gmail", "https://mail.google.com"), new("YouTube", "https://www.youtube.com"), new("Calendar", "https://calendar.google.com")],
        ["firefox"] = [new("Gmail", "https://mail.google.com"), new("YouTube", "https://www.youtube.com"), new("Calendar", "https://calendar.google.com")],
        ["gmail"] = [new("Compose", "https://mail.google.com/mail/?view=cm"), new("Unread", "https://mail.google.com/mail/u/0/#search/is%3Aunread")],
        ["outlook"] = [new("New email", "mailto:")],
        ["mail"] = [new("New email", "mailto:")],
        ["youtube"] = [new("Subscriptions", "https://www.youtube.com/feed/subscriptions"), new("Watch later", "https://www.youtube.com/playlist?list=WL")],
        ["github"] = [new("Your pull requests", "https://github.com/pulls"), new("Notifications", "https://github.com/notifications")],
        ["visual studio code"] = [new("New window", "vscode://")],
        ["whatsapp"] = [new("New chat", "whatsapp://send")],
        ["file explorer"] = [new("Downloads", "shell:Downloads"), new("Documents", "shell:Personal")],
    };

    // "can you", "please", "for me", "hey jarvis" and the like around the request.
    [GeneratedRegex(@"^(hey |ok |okay )?(jarvis[, ]+)?(can you |could you |would you |please |pls |just )*|\s+(for me|please|now|real quick)$", RegexOptions.IgnoreCase)]
    private static partial Regex Polite();

    [GeneratedRegex(@"^(open( up)?|launch|start|run|go to|bring up|pull up)\s+(?<what>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Open();

    [GeneratedRegex(@"^(search( for)?|look up|google)\s+(?<what>.+?)(\s+(on|in)\s+(?<where>youtube|spotify|github|amazon|google maps|maps|wikipedia|google))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Search();

    [GeneratedRegex(@"^play\s+(?<what>.+?)\s+on\s+spotify$", RegexOptions.IgnoreCase)]
    private static partial Regex Play();

    [GeneratedRegex(@"^(the|my|app|application)\s+|\s+(app|application)$", RegexOptions.IgnoreCase)]
    private static partial Regex Filler();

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)+(/\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LooksLikeAddress();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
