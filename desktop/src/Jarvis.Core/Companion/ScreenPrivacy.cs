using System.Globalization;
using Microsoft.Extensions.Options;

namespace Jarvis.Core.Companion;

/// <summary>Privacy settings, bound from the "Privacy" settings section.</summary>
public sealed class PrivacyOptions
{
    public const string SectionName = "Privacy";

    /// <summary>Off: Jarvis never takes a screenshot; it answers from what you say alone.</summary>
    public bool ScreenAccess { get; set; } = true;

    /// <summary>Say "Looking at your screen" in the notch each time a screenshot is taken.</summary>
    public bool ShowWhenLooking { get; set; } = true;

    /// <summary>
    /// Let screen recordings and screenshots (Jarvis's own included) see the cursor and notch.
    /// Read once at start-up.
    /// </summary>
    public bool ShowInRecordings { get; set; }

    /// <summary>
    /// Let Jarvis do steps with the real mouse and keyboard when asked. Every step still needs the
    /// user's click on "Do it"; there is no way to approve steps in advance.
    /// </summary>
    public bool LetJarvisDrive { get; set; }

    /// <summary>
    /// True (default): Jarvis asks before every step. False: it asks once per task, then works on
    /// its own with the control border showing, until done or the user takes back.
    /// </summary>
    public bool AskEveryStep { get; set; } = true;

    /// <summary>
    /// Everything is answered on this PC by the local model: nothing goes to Claude, except a
    /// screenshot the user agrees to send when a question needs the screen. Background jobs,
    /// routines and suggestions (which run on Claude) wait until it's off.
    /// </summary>
    public bool PrivateMode { get; set; }
}

/// <summary>One screenshot Jarvis took: when, how many displays, and why.</summary>
public sealed record ScreenLook(DateTimeOffset At, int Displays, string Reason);

/// <summary>
/// The rule and the record for screen access. Jarvis only ever looks at the screen when you ask it
/// something; every look is written to a local log (never sent anywhere), so you can check it.
/// </summary>
public sealed class ScreenPrivacy
{
    private const int Kept = 500;

    private readonly IOptionsMonitor<PrivacyOptions> _options;
    private readonly string _logPath;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    public ScreenPrivacy(IOptionsMonitor<PrivacyOptions> options, string logPath, TimeProvider time)
    {
        _options = options;
        _logPath = logPath;
        _time = time;
    }

    /// <summary>Raised after each look is recorded (on the capturing thread).</summary>
    public event Action<ScreenLook>? Looked;

    public bool ScreenAccess => _options.CurrentValue.ScreenAccess;

    public bool ShowWhenLooking => _options.CurrentValue.ShowWhenLooking;

    public bool LetJarvisDrive => _options.CurrentValue.LetJarvisDrive;

    public bool AskEveryStep => _options.CurrentValue.AskEveryStep;

    public bool PrivateMode => _options.CurrentValue.PrivateMode;

    public void Record(int displays, string reason)
    {
        var look = new ScreenLook(_time.GetUtcNow(), displays, reason);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllLines(_logPath, [$"{look.At:O}\t{look.Displays}\t{look.Reason}"]);
        }

        Looked?.Invoke(look);
    }

    /// <summary>The most recent looks, newest first.</summary>
    public IReadOnlyList<ScreenLook> Recent(int count = 50)
    {
        lock (_gate)
        {
            if (!File.Exists(_logPath))
            {
                return [];
            }

            var lines = File.ReadAllLines(_logPath);
            if (lines.Length > Kept * 2)
            {
                lines = lines[^Kept..];
                File.WriteAllLines(_logPath, lines); // keep the file small
            }

            return [.. Enumerable.Reverse(lines).Take(count).Select(Parse).OfType<ScreenLook>()];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            File.Delete(_logPath);
        }
    }

    private static ScreenLook? Parse(string line)
    {
        var parts = line.Split('\t');
        return parts.Length == 3
               && DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
               && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var displays)
            ? new ScreenLook(at, displays, parts[2])
            : null;
    }
}
