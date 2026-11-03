namespace Jarvis.Core.Input;

/// <summary>
/// Recognises modifier-only hold shortcuts from raw key events: hold Ctrl+Alt to talk, Ctrl+Win to
/// dictate. Steps aside when the user is really typing a shortcut (Ctrl+Alt+T, Win+Ctrl+→, AltGr
/// characters): any other key during a hold cancels it, and nothing re-arms until every modifier
/// is up. Escape cancels whatever is running. Double-tapping Ctrl on its own opens the text box;
/// double-tapping Ctrl+Win dictates hands-free until the next chord press.
/// </summary>
public sealed class ChordDetector
{
    private const int Escape = 0x1B;

    /// <summary>Longest press that still counts as a tap (DESIGN-FACTS.md §4).</summary>
    private static readonly TimeSpan TapLength = TimeSpan.FromMilliseconds(400);

    /// <summary>Longest gap between the first tap's release and the second tap's press.</summary>
    private static readonly TimeSpan TapGap = TimeSpan.FromMilliseconds(500);

    /// <summary>Hands-free dictation: the second Ctrl+Win tap must start this soon after the first ends.</summary>
    private static readonly TimeSpan LockGap = TimeSpan.FromMilliseconds(400);

    private Modifiers _held;
    private Chord _active;
    private bool _spoiled;
    private TimeSpan? _ctrlDownAt;
    private TimeSpan? _lastTapAt;
    private TimeSpan _chordAt;
    private TimeSpan? _lastDictateTapAt;
    private bool _dictationLocked;
    private (Modifiers Talk, Modifiers Dictate) _chords = (Modifiers.Ctrl | Modifiers.Alt, Modifiers.Ctrl | Modifiers.Win);

    [Flags]
    private enum Modifiers
    {
        None = 0,
        Ctrl = 1,
        Alt = 2,
        Win = 4,
        Shift = 8,
    }

    /// <summary>
    /// Sets the hold chords, e.g. "Alt + Win". Ignored unless both are valid two-modifier pairs and
    /// differ. Callable from any thread; a key event racing the change sees the old or new pair.
    /// </summary>
    public void UseChords(string talk, string dictate)
    {
        if (Parse(talk) is var talkKeys and not Modifiers.None && Parse(dictate) is var dictateKeys and not Modifiers.None && talkKeys != dictateKeys)
        {
            _chords = (talkKeys, dictateKeys);
        }
    }

    /// <param name="virtualKey">Windows virtual-key code.</param>
    /// <param name="isDown">Key down (including auto-repeat) or key up.</param>
    /// <param name="at">When the key event happened, on any steady clock (only differences matter).</param>
    public ChordEvent OnKey(int virtualKey, bool isDown, TimeSpan at = default)
    {
        if (ModifierOf(virtualKey) is var modifier and not Modifiers.None)
        {
            var before = _held;
            _held = isDown ? _held | modifier : _held & ~modifier;
            if (_held == before)
            {
                return ChordEvent.None; // auto-repeat
            }

            if (OnCtrlTap(before, at))
            {
                return new ChordEvent(Chord.Compose, ChordSignal.Pressed);
            }

            if (_active != Chord.None)
            {
                // Any change to the held set ends the chord; holding the rest must not start another.
                var ended = _active;
                _active = Chord.None;
                _spoiled = _held != Modifiers.None;
                return ended == Chord.Dictate && LocksDictation(at)
                    ? ChordEvent.None // second quick tap: keep recording without the keys
                    : new ChordEvent(ended, ChordSignal.Released);
            }

            if (_held == Modifiers.None)
            {
                _spoiled = false;
            }
            else if (!_spoiled && ChordFor(_held) is var chord and not Chord.None)
            {
                if (_dictationLocked)
                {
                    // Any chord finishes hands-free dictation; this press does nothing else.
                    _dictationLocked = false;
                    _spoiled = true;
                    return new ChordEvent(Chord.Dictate, ChordSignal.Released);
                }

                _active = chord;
                _chordAt = at;
                return new ChordEvent(chord, ChordSignal.Pressed);
            }

            return ChordEvent.None;
        }

        if (!isDown)
        {
            return ChordEvent.None;
        }

        // Typing between or during taps: it was a shortcut or just text, not a double-tap.
        _ctrlDownAt = null;
        _lastTapAt = null;
        _lastDictateTapAt = null;

        if (_held != Modifiers.None)
        {
            _spoiled = true;
        }

        if (_active != Chord.None)
        {
            var cancelled = _active;
            _active = Chord.None;
            return new ChordEvent(cancelled, ChordSignal.Cancelled);
        }

        if (virtualKey == Escape)
        {
            _dictationLocked = false;
        }

        return virtualKey == Escape ? new ChordEvent(Chord.None, ChordSignal.Cancelled) : ChordEvent.None;
    }

    /// <summary>Tracks lone Ctrl taps; true when this release completes a double-tap.</summary>
    private bool OnCtrlTap(Modifiers before, TimeSpan at)
    {
        if (before == Modifiers.None && _held == Modifiers.Ctrl)
        {
            _ctrlDownAt = at;
            return false;
        }

        if (before == Modifiers.Ctrl && _held == Modifiers.None && _ctrlDownAt is { } downAt)
        {
            _ctrlDownAt = null;
            if (!IsWithin(at - downAt, TapLength))
            {
                _lastTapAt = null;
                return false;
            }

            var isDouble = _lastTapAt is { } lastAt && IsWithin(downAt - lastAt, TapGap);
            _lastTapAt = isDouble ? null : at;
            return isDouble;
        }

        // Another modifier joined in: a chord, not a tap.
        _ctrlDownAt = null;
        _lastTapAt = null;
        return false;
    }

    /// <summary>True when this Ctrl+Win release completes a double-tap (hands-free dictation).</summary>
    private bool LocksDictation(TimeSpan at)
    {
        if (!IsWithin(at - _chordAt, TapLength))
        {
            _lastDictateTapAt = null;
            return false;
        }

        _dictationLocked = _lastDictateTapAt is { } lastAt && IsWithin(_chordAt - lastAt, LockGap);
        _lastDictateTapAt = _dictationLocked ? null : at;
        return _dictationLocked;
    }

    // Negative spans happen when the system tick count wraps (every 49.7 days).
    private static bool IsWithin(TimeSpan span, TimeSpan limit) => span >= TimeSpan.Zero && span <= limit;

    private Chord ChordFor(Modifiers held)
    {
        var (talk, dictate) = _chords;
        return held == talk ? Chord.Talk : held == dictate ? Chord.Dictate : Chord.None;
    }

    /// <summary>"Ctrl + Alt" → Ctrl|Alt; <see cref="Modifiers.None"/> unless exactly two distinct modifiers.</summary>
    private static Modifiers Parse(string? chord)
    {
        var parts = (chord ?? string.Empty).Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var keys = Modifiers.None;
        foreach (var part in parts)
        {
            keys |= part.ToUpperInvariant() switch
            {
                "CTRL" => Modifiers.Ctrl,
                "ALT" => Modifiers.Alt,
                "WIN" => Modifiers.Win,
                "SHIFT" => Modifiers.Shift,
                _ => Modifiers.None,
            };
        }

        return parts.Length == 2 && System.Numerics.BitOperations.PopCount((uint)keys) == 2 ? keys : Modifiers.None;
    }

    private static Modifiers ModifierOf(int key) => key switch
    {
        0x11 or 0xA2 or 0xA3 => Modifiers.Ctrl,
        0x12 or 0xA4 or 0xA5 => Modifiers.Alt,
        0x5B or 0x5C => Modifiers.Win,
        0x10 or 0xA0 or 0xA1 => Modifiers.Shift,
        _ => Modifiers.None,
    };
}

public enum Chord
{
    /// <summary>No particular chord (Escape cancels everything).</summary>
    None,

    /// <summary>Hold Ctrl+Alt: ask Jarvis.</summary>
    Talk,

    /// <summary>Hold Ctrl+Win: dictate into the focused field.</summary>
    Dictate,

    /// <summary>Double-tap Ctrl: type a question. Only ever <see cref="ChordSignal.Pressed"/>.</summary>
    Compose,
}

public enum ChordSignal
{
    None,

    /// <summary>The chord is held: start.</summary>
    Pressed,

    /// <summary>The chord was let go: finish.</summary>
    Released,

    /// <summary>Abandon: another key was pressed during the hold, or Escape.</summary>
    Cancelled,
}

public readonly record struct ChordEvent(Chord Chord, ChordSignal Signal)
{
    public static ChordEvent None { get; } = new(Chord.None, ChordSignal.None);
}
