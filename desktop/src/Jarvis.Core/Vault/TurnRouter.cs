using System.Text.RegularExpressions;

namespace Jarvis.Core.Vault;

/// <summary>Who answers a turn.</summary>
public enum TurnRoute
{
    /// <summary>Claude, with the screenshot (the default; never sees the vault).</summary>
    Cloud,

    /// <summary>The local model on this PC, without the screen (private mode).</summary>
    Local,

    /// <summary>The vault's own brain on this PC: the question is about the user's notes.</summary>
    Vault,

    /// <summary>Fill the browser's form from the vault on this PC.</summary>
    Form,

    /// <summary>The user's calendar, read from its iCal feed on this PC (no screen, no model).</summary>
    Calendar,

    /// <summary>A new event for the user's calendar, shown to them to check before it goes in.</summary>
    CalendarAdd,
}

/// <summary>
/// Decides before anything leaves the PC. Anything about the vault, or a form to fill, stays
/// local in every mode; private mode keeps everything else local too.
/// </summary>
/// <remarks>
/// ponytail: keyword match. A miss sends only the question to Claude, which has no vault access
/// and says it doesn't know; a local-model classifier is the upgrade if misses matter.
/// </remarks>
public static partial class TurnRouter
{
    /// <param name="browserInFront">A browser is the app in front: "fill this in" then means its page.</param>
    public static TurnRoute Route(string request, bool privateMode, bool browserInFront = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        return !AsksAdvice(request) && (FormRequest().IsMatch(request) || (browserInFront && FillThis().IsMatch(request))) ? TurnRoute.Form
            : CalendarAdd().IsMatch(request) ? TurnRoute.CalendarAdd
            : CalendarRequest().IsMatch(request) && !CalendarChange().IsMatch(request) ? TurnRoute.Calendar
            : VaultRequest().IsMatch(request) ? TurnRoute.Vault
            : privateMode ? TurnRoute.Local
            : TurnRoute.Cloud;
    }

    /// <summary>
    /// Sounds like it could be about a form, allowing for how speech-to-text mishears "fill" ("feel",
    /// "Phil") and "form" ("farm"). Too loose to route on alone: the caller also checks that the page
    /// in front really has a form.
    /// </summary>
    public static bool MightMeanForm(string request) => FormHint().IsMatch(request ?? string.Empty);

    [GeneratedRegex(@"\b(f(i|ee|ea|e)l+(s|ed|ing)?|fil|phil+s?|field|forms?|farms?|foam|forum|applications?|surveys?|questionnaires?|auto[- ]?fill)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FormHint();

    /// <summary>
    /// A question for an opinion or an explanation ("which should I pick?", "what does this mean?",
    /// "what do you think"): answered, never taken as "fill this form", even with a form on screen.
    /// </summary>
    public static bool AsksAdvice(string request) => Advice().IsMatch(request ?? string.Empty);

    [GeneratedRegex(@"\b(should i|which (one|option|should)|what do you (think|suggest|recommend)|do you (think|recommend)|recommend|suggest|what does|what is|what's the difference|explain|meaning of|mean\b|why|or\s+\w+(\s+\w+)?\s*\?\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex Advice();

    /// <summary>The user wants Jarvis to put the answers into the page, not just show them.</summary>
    public static bool WantsFillIn(string request) => FillIn().IsMatch(request ?? string.Empty);

    [GeneratedRegex(@"\btake (over|control)\b|\bfor me\b|\b(type|enter|put|fill)\s+(it|them|those|the answers|everything)\s+in\b|\bfill (it|them|everything) (in|out)\b|\bauto ?fill\b", RegexOptions.IgnoreCase)]
    private static partial Regex FillIn();

    // "Phil" is how speech-to-text often hears "fill".
    [GeneratedRegex(@"\b(fill|phil|complete|answers?|do|finish|populate|help(\s+me)?(\s+with)?|take care of|work on)\b[^.?!]{0,40}\b(forms?|applications?|questionnaires?|surveys?|sign[- ]?up( sheet)?)\b|\bforms?\b[^.?!]{0,30}\b(fill|answers?)\b|\bauto[- ]?fill\b", RegexOptions.IgnoreCase)]
    private static partial Regex FormRequest();

    [GeneratedRegex(@"\b(fill|phil|complete)\s+((it|this|these|that|them|everything)\b|(in|out|up)\s+(it|this|these|the)\b|(the|these|my)\s+(details|fields|blanks|questions|answers)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FillThis();

    // "What's on my calendar on the 26th", "any meetings tomorrow?", "am I free on Friday".
    [GeneratedRegex(@"\b(calendar|agenda|schedule|meetings?|appointments?)\b|\bam I (free|busy)\b|\bwhat'?s on\b[^.?!]{0,30}\b(today|tomorrow|tonight|this week|next week|monday|tuesday|wednesday|thursday|friday|saturday|sunday|\d{1,2}(st|nd|rd|th)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CalendarRequest();

    // "Add lunch with Sam tomorrow at 1 to my calendar", "schedule a call with the bank on Friday".
    [GeneratedRegex(@"\b(add|put|schedule|create|book|set up|block)\b[^.?!]{0,80}\b(to|on|in|into)\s+(my\s+)?calendar\b|\b(schedule|book|create|add|set up)\s+(a|an|the|my)?\s*(meeting|event|call|appointment)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CalendarAdd();

    // Changing or cancelling events isn't reading the feed (and an iCal address is read-only).
    [GeneratedRegex(@"\b(schedule|book|create|add|set up|move|reschedule|cancel|invite)\s+(a|an|the|my|this)?\s*(meeting|event|call|appointment|invite)", RegexOptions.IgnoreCase)]
    private static partial Regex CalendarChange();

    // Also what the user has or studies ("how many modules do I have in cog"), but not "do I have to…".
    [GeneratedRegex(@"\bvault\b|\b(my|from my|in my)\s+(notes?|profile|journal|diary|resume|cv|modules?|courses?|subjects?|lectures?|assignments?|documents?|exams?|grades?)\b|\bstandard answers?\b|\bI\s+(wrote|saved|noted|stored)\b|\b(do|did)\s+I\s+(have(?!\s+to\b)|take|study|own)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VaultRequest();
}
