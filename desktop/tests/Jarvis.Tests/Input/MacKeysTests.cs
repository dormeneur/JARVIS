using Jarvis.Core.Input;
using Jarvis.Platform.Mac.Input;

namespace Jarvis.Tests.Input;

/// <summary>macOS key events, translated for the shared ChordDetector (no native code involved).</summary>
public sealed class MacKeysTests
{
    // macOS key codes and the device bits a "flags changed" event carries for them.
    private const int LeftControl = 59;
    private const int LeftOption = 58;
    private const int LeftCommand = 55;
    private const int KeyT = 17;
    private const ulong LeftControlFlags = MacKeys.Control | 0x1;
    private const ulong LeftOptionFlags = MacKeys.Option | 0x20;
    private const ulong LeftCommandFlags = MacKeys.Command | 0x8;

    private static IEnumerable<ChordEvent> Feed(ChordDetector detector, params (int Kind, int Key, ulong Flags, double Ms)[] events)
    {
        ulong held = 0;
        foreach (var (kind, key, flags, ms) in events)
        {
            foreach (var (virtualKey, isDown) in MacKeys.Translate(kind, key, flags, ref held))
            {
                var chord = detector.OnKey(virtualKey, isDown, TimeSpan.FromMilliseconds(ms));
                if (chord.Signal != ChordSignal.None)
                {
                    yield return chord;
                }
            }
        }
    }

    [Fact]
    public void ControlOption_IsTalk_PressedThenReleased()
    {
        var events = Feed(
            new ChordDetector(),
            (MacKeys.FlagsChanged, LeftControl, LeftControlFlags, 0),
            (MacKeys.FlagsChanged, LeftOption, LeftControlFlags | LeftOptionFlags, 10),
            (MacKeys.FlagsChanged, LeftOption, LeftControlFlags, 900),
            (MacKeys.FlagsChanged, LeftControl, 0, 910)).ToList();

        Assert.Equal([new ChordEvent(Chord.Talk, ChordSignal.Pressed), new ChordEvent(Chord.Talk, ChordSignal.Released)], events);
    }

    [Fact]
    public void ControlCommand_IsDictate()
    {
        var events = Feed(
            new ChordDetector(),
            (MacKeys.FlagsChanged, LeftControl, LeftControlFlags, 0),
            (MacKeys.FlagsChanged, LeftCommand, LeftControlFlags | LeftCommandFlags, 10)).ToList();

        Assert.Equal([new ChordEvent(Chord.Dictate, ChordSignal.Pressed)], events);
    }

    [Fact]
    public void DoubleTapControl_Composes()
    {
        var events = Feed(
            new ChordDetector(),
            (MacKeys.FlagsChanged, LeftControl, LeftControlFlags, 0),
            (MacKeys.FlagsChanged, LeftControl, 0, 100),
            (MacKeys.FlagsChanged, LeftControl, LeftControlFlags, 300),
            (MacKeys.FlagsChanged, LeftControl, 0, 400)).ToList(); // the second release completes it

        Assert.Equal([new ChordEvent(Chord.Compose, ChordSignal.Pressed)], events);
    }

    [Fact]
    public void TypingAShortcut_SpoilsTheChord_AndEscapeCancels()
    {
        var detector = new ChordDetector();
        var typed = Feed(
            detector,
            (MacKeys.FlagsChanged, LeftControl, LeftControlFlags, 0),
            (MacKeys.KeyDown, KeyT, LeftControlFlags, 5),
            (MacKeys.FlagsChanged, LeftOption, LeftControlFlags | LeftOptionFlags, 10)).ToList();
        Assert.Empty(typed); // Control+T then Option: the user is typing, not talking

        var escape = Feed(new ChordDetector(), (MacKeys.KeyDown, 53, 0, 0)).ToList();
        Assert.Equal([new ChordEvent(Chord.None, ChordSignal.Cancelled)], escape);
    }

    [Fact]
    public void CapsLockAndFn_AreIgnored()
    {
        ulong held = 0;
        Assert.Empty(MacKeys.Translate(MacKeys.FlagsChanged, 57, 0x10000, ref held));
        Assert.Empty(MacKeys.Translate(MacKeys.FlagsChanged, 63, 0x800000, ref held));
    }

    [Theory]
    [InlineData("cmd+s", 1, MacKeys.Command)]
    [InlineData("ctrl+shift+tab", 48, MacKeys.Control | MacKeys.Shift)]
    [InlineData("win+space", 49, MacKeys.Command)]
    [InlineData("enter", 36, 0UL)]
    [InlineData("F5", 96, 0UL)]
    [InlineData("alt+9", 25, MacKeys.Option)]
    public void Combinations_Parse(string combination, int key, ulong flags)
        => Assert.Equal((key, flags), MacKeys.ParseCombination(combination));

    [Theory]
    [InlineData("cmd+banana")]
    [InlineData("cmd+")]
    [InlineData("a+b")]
    public void BadCombinations_AreRefused(string combination)
        => Assert.Null(MacKeys.ParseCombination(combination));
}
