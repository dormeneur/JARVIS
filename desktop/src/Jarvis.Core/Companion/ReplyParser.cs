using Jarvis.Contracts.Control;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jarvis.Core.Drawing;

namespace Jarvis.Core.Companion;

/// <summary>
/// Turns a streamed reply into things to do as soon as they are complete: sentences to speak,
/// <c>[POINT:x,y:label:screenN]</c> tags to fly the cursor to, <c>[DRAW]…[/DRAW]</c> blocks to
/// sketch on screen, <c>[AGENT]…[/AGENT]</c> briefs for background jobs, and
/// <c>[ROUTINE:schedule]…[/ROUTINE]</c> briefs for recurring ones. Tags and blocks never
/// reach the speech.
/// Feed it deltas in order, then call <see cref="Complete"/>.
/// </summary>
public sealed partial class ReplyParser
{
    private readonly StringBuilder _pending = new();

    public IReadOnlyList<ReplyItem> Feed(string delta)
    {
        _pending.Append(delta);
        return Drain(final: false);
    }

    /// <summary>Flushes whatever is left once the reply has ended.</summary>
    public IReadOnlyList<ReplyItem> Complete() => Drain(final: true);

    private List<ReplyItem> Drain(bool final)
    {
        var items = new List<ReplyItem>();
        var text = _pending.ToString();

        foreach (Match tag in PointTag().Matches(text))
        {
            if (ParsePoint(tag) is { } point)
            {
                items.Add(point);
            }
        }

        text = PointTag().Replace(text, string.Empty);

        foreach (Match tag in ActTag().Matches(text))
        {
            if (ParseAct(tag) is { } act)
            {
                items.Add(act);
            }
        }

        text = ActTag().Replace(text, string.Empty);

        if (MoreTag().IsMatch(text))
        {
            items.Add(new ReplyItem.More());
            text = MoreTag().Replace(text, string.Empty);
        }

        foreach (Match block in DrawBlock().Matches(text))
        {
            var screen = block.Groups["screen"].Success ? int.Parse(block.Groups["screen"].Value, CultureInfo.InvariantCulture) : 1;
            items.Add(new ReplyItem.Drawing(DrawingScript.Parse(block.Groups["body"].Value, screen)));
        }

        text = DrawBlock().Replace(text, string.Empty);

        foreach (Match block in AgentBlock().Matches(text))
        {
            if (block.Groups["brief"].Value.Trim() is { Length: > 0 } brief)
            {
                items.Add(new ReplyItem.Delegate(brief));
            }
        }

        text = AgentBlock().Replace(text, string.Empty);

        foreach (Match block in RoutineBlock().Matches(text))
        {
            if (block.Groups["brief"].Value.Trim() is { Length: > 0 } brief)
            {
                items.Add(new ReplyItem.Recurring(block.Groups["schedule"].Value.Trim(), brief));
            }
        }

        text = RoutineBlock().Replace(text, string.Empty);

        // A block that has opened but not closed is never spoken; hold it until it closes.
        var openBlock = OpenBlock().Match(text) is { Success: true } open ? open.Index : -1;
        if (openBlock >= 0 && !final)
        {
            var heldBlock = text[openBlock..];
            text = text[..openBlock];
            var drained = DrainSpeech(items, text, final: false);
            _pending.Clear().Append(drained).Append(heldBlock);
            return items;
        }

        if (openBlock >= 0)
        {
            text = text[..openBlock]; // the reply ended inside an unclosed block: drop it
        }

        _pending.Clear().Append(DrainSpeech(items, text, final));
        return items;
    }

    /// <summary>Emits complete sentences from <paramref name="text"/>; returns what must wait for more text.</summary>
    private static string DrainSpeech(List<ReplyItem> items, string text, bool final)
    {
        // Hold back a tag that has started but not finished, so its digits are never spoken.
        var openTag = text.LastIndexOf('[');
        var held = openTag >= 0 && !final && text.IndexOf(']', openTag) < 0 ? text[openTag..] : string.Empty;
        var speakable = held.Length > 0 ? text[..openTag] : text;

        var consumed = 0;
        foreach (Match boundary in SentenceEnd().Matches(speakable))
        {
            var end = boundary.Index + boundary.Length;
            AddLine(items, speakable[consumed..end]);
            consumed = end;
        }

        var rest = speakable[consumed..];
        if (final)
        {
            AddLine(items, rest + held);
            return string.Empty;
        }

        return rest + held;
    }

    private static void AddLine(List<ReplyItem> items, string text)
    {
        var line = Whitespace().Replace(text, " ").Trim();
        if (line.Length > 0)
        {
            items.Add(new ReplyItem.Line(line));
        }
    }

    private static ReplyItem.Point? ParsePoint(Match tag)
    {
        if (!double.TryParse(tag.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(tag.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return null;
        }

        var screen = tag.Groups["screen"].Success ? int.Parse(tag.Groups["screen"].Value, CultureInfo.InvariantCulture) : 1;
        return new ReplyItem.Point(x, y, tag.Groups["label"].Value.Trim(), screen);
    }

    private static ReplyItem.Act? ParseAct(Match tag)
    {
        var kind = tag.Groups["kind"].Value.ToUpperInvariant() switch
        {
            "CLICK" => ComputerActionKind.Click,
            "DOUBLECLICK" => ComputerActionKind.DoubleClick,
            "RIGHTCLICK" => ComputerActionKind.RightClick,
            "SCROLLUP" => ComputerActionKind.ScrollUp,
            "SCROLLDOWN" => ComputerActionKind.ScrollDown,
            "TYPE" => ComputerActionKind.Type,
            "OPEN" => ComputerActionKind.Open,
            _ => ComputerActionKind.Key,
        };
        if (kind is ComputerActionKind.Type or ComputerActionKind.Key or ComputerActionKind.Open)
        {
            return tag.Groups["text"].Value is { Length: > 0 } text ? new ReplyItem.Act(kind, 0, 0, string.Empty, 1, text) : null;
        }

        if (!double.TryParse(tag.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(tag.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return null;
        }

        var screen = tag.Groups["screen"].Success ? int.Parse(tag.Groups["screen"].Value, CultureInfo.InvariantCulture) : 1;
        return new ReplyItem.Act(kind, x, y, tag.Groups["label"].Value.Trim(), screen, null);
    }

    /// <summary>[DO:click x,y:label:screenN] (doubleclick, rightclick, scrollup, scrolldown), [DO:type:text], [DO:key:ctrl+s].</summary>
    [GeneratedRegex(@"\[DO:(?:(?<kind>click|doubleclick|rightclick|scrollup|scrolldown)[\s:]+(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)(?::(?<label>[^:\]]*))?(?::screen(?<screen>\d+))?|(?<kind>type|key|open):(?<text>[^\]]*))\]", RegexOptions.IgnoreCase)]
    private static partial Regex ActTag();

    /// <summary>[MORE]: after these steps the task continues from a fresh look at the screen.</summary>
    [GeneratedRegex(@"\[MORE\]", RegexOptions.IgnoreCase)]
    private static partial Regex MoreTag();

    /// <summary>[AGENT] brief for a background job [/AGENT].</summary>
    [GeneratedRegex(@"\[AGENT\](?<brief>.*?)\[/AGENT\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AgentBlock();

    /// <summary>[ROUTINE:schedule] brief for a recurring job [/ROUTINE].</summary>
    [GeneratedRegex(@"\[ROUTINE:(?<schedule>[^\]]+)\](?<brief>.*?)\[/ROUTINE\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RoutineBlock();

    /// <summary>The start of a [DRAW…], [AGENT] or [ROUTINE…] block.</summary>
    [GeneratedRegex(@"\[(?:DRAW|AGENT|ROUTINE)", RegexOptions.IgnoreCase)]
    private static partial Regex OpenBlock();

    /// <summary>[DRAW] or [DRAW:screenN], then commands, then [/DRAW].</summary>
    [GeneratedRegex(@"\[DRAW(?::screen(?<screen>\d+))?\](?<body>.*?)\[/DRAW\]", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DrawBlock();

    /// <summary>[POINT:x,y], optionally followed by :label and :screenN; also [POINT:none].</summary>
    [GeneratedRegex(@"\[POINT:(?:none|(?<x>-?\d+(?:\.\d+)?)\s*,\s*(?<y>-?\d+(?:\.\d+)?)(?::(?<label>[^:\]]*))?(?::screen(?<screen>\d+))?)\]", RegexOptions.IgnoreCase)]
    private static partial Regex PointTag();

    /// <summary>End of a sentence: terminal punctuation (plus closing quotes) before whitespace, or a line break.</summary>
    [GeneratedRegex(@"[.!?…]+[""')\]]*(?=\s)|\n+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Something the reply asked for.</summary>
public abstract record ReplyItem
{
    private ReplyItem()
    {
    }

    /// <summary>A sentence to speak and caption.</summary>
    public sealed record Line(string Text) : ReplyItem;

    /// <param name="X">Image pixels on screen <paramref name="Screen"/>.</param>
    /// <param name="Y">Image pixels on screen <paramref name="Screen"/>.</param>
    /// <param name="Screen">1-based, as numbered in the prompt.</param>
    public sealed record Point(double X, double Y, string Label, int Screen) : ReplyItem;

    /// <summary>Marks to draw over a screen.</summary>
    public sealed record Drawing(DrawingScript Script) : ReplyItem;

    /// <summary>Hand this work to a background agent; <paramref name="Brief"/> is what it should do.</summary>
    public sealed record Delegate(string Brief) : ReplyItem;

    /// <summary>A step on the user's real mouse or keyboard; done only if they approve it.</summary>
    /// <param name="X">Image pixels on screen <paramref name="Screen"/> (pointer steps).</param>
    /// <param name="Y">Image pixels on screen <paramref name="Screen"/> (pointer steps).</param>
    /// <param name="Text">What to type, or the key combination.</param>
    public sealed record Act(ComputerActionKind Kind, double X, double Y, string Label, int Screen, string? Text) : ReplyItem;

    /// <summary>The task needs another look at the screen after these steps.</summary>
    public sealed record More : ReplyItem;

    /// <summary>Run this background job on a schedule (see <see cref="Agents.RoutineSchedule"/>).</summary>
    public sealed record Recurring(string Schedule, string Brief) : ReplyItem;
}
