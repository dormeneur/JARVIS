using Jarvis.Core.Input;

namespace Jarvis.Tests.Input;

public sealed class ChordDetectorTests
{
    private const int LeftCtrl = 0xA2;
    private const int LeftAlt = 0xA4;
    private const int RightAlt = 0xA5;
    private const int LeftWin = 0x5B;
    private const int LeftShift = 0xA0;
    private const int KeyT = 0x54;
    private const int Right = 0x27;
    private const int Escape = 0x1B;

    [Fact]
    public void CtrlAlt_IsTalk_PressedThenReleased()
    {
        var detector = new ChordDetector();

        Assert.Equal(ChordEvent.None, detector.OnKey(LeftCtrl, true));
        Assert.Equal(new ChordEvent(Chord.Talk, ChordSignal.Pressed), detector.OnKey(LeftAlt, true));
        Assert.Equal(ChordEvent.None, detector.OnKey(LeftAlt, true)); // auto-repeat
        Assert.Equal(new ChordEvent(Chord.Talk, ChordSignal.Released), detector.OnKey(LeftCtrl, false));
        Assert.Equal(ChordEvent.None, detector.OnKey(LeftAlt, false));
    }

    [Fact]
    public void CtrlWin_IsDictate()
    {
        var detector = new ChordDetector();

        detector.OnKey(LeftWin, true);
        Assert.Equal(new ChordEvent(Chord.Dictate, ChordSignal.Pressed), detector.OnKey(LeftCtrl, true));
        Assert.Equal(new ChordEvent(Chord.Dictate, ChordSignal.Released), detector.OnKey(LeftWin, false));
    }

    [Fact]
    public void ShortcutDuringHold_CancelsAndDoesNotRestart()
    {
        var detector = new ChordDetector();
        detector.OnKey(LeftCtrl, true);
        detector.OnKey(LeftWin, true);

        Assert.Equal(new ChordEvent(Chord.Dictate, ChordSignal.Cancelled), detector.OnKey(Right, true)); // Win+Ctrl+→ switches desktop
        Assert.Equal(ChordEvent.None, detector.OnKey(LeftWin, false));
        Assert.Equal(ChordEvent.None, detector.OnKey(LeftWin, true)); // Ctrl still held: stays spoiled
    }

    [Fact]
    public void KeyBeforeChordCompletes_SpoilsIt()
    {
        var detector = new ChordDetector();
        detector.OnKey(LeftCtrl, true);
        detector.OnKey(KeyT, true);

        Assert.Equal(ChordEvent.None, detector.OnKey(RightAlt, true));
    }

    [Fact]
    public void AddingAModifier_EndsTheChord_WithoutStartingAnother()
    {
        var detector = new ChordDetector();
        detector.OnKey(LeftCtrl, true);
        detector.OnKey(LeftAlt, true);

        Assert.Equal(new ChordEvent(Chord.Talk, ChordSignal.Released), detector.OnKey(LeftWin, true));
        Assert.Equal(ChordEvent.None, detector.OnKey(LeftAlt, false)); // Ctrl+Win now held, but spoiled
    }

    [Fact]
    public void WorksAgainOnceEveryModifierIsUp()
    {
        var detector = new ChordDetector();
        detector.OnKey(LeftCtrl, true);
        detector.OnKey(KeyT, true);
        detector.OnKey(KeyT, false);
        detector.OnKey(LeftCtrl, false);

        detector.OnKey(LeftCtrl, true);
        Assert.Equal(new ChordEvent(Chord.Talk, ChordSignal.Pressed), detector.OnKey(LeftAlt, true));
    }

    [Fact]
    public void Escape_CancelsEverythingWhenIdle()
        => Assert.Equal(new ChordEvent(Chord.None, ChordSignal.Cancelled), new ChordDetector().OnKey(Escape, true));

    [Fact]
    public void DoubleTapCtrl_IsCompose()
    {
        var detector = new ChordDetector();

        Assert.Equal(ChordEvent.None, Tap(detector, LeftCtrl, 0, 120));
        Assert.Equal(new ChordEvent(Chord.Compose, ChordSignal.Pressed), Tap(detector, LeftCtrl, 400, 500));
        Assert.Equal(ChordEvent.None, Tap(detector, LeftCtrl, 700, 800)); // a third tap starts over
    }

    [Theory]
    [InlineData(0, 450, 600, 700)] // first press too long to be a tap
    [InlineData(0, 100, 650, 700)] // second tap came too late
    public void SlowTaps_AreNotCompose(int down1, int up1, int down2, int up2)
    {
        var detector = new ChordDetector();
        Tap(detector, LeftCtrl, down1, up1);

        Assert.Equal(ChordEvent.None, Tap(detector, LeftCtrl, down2, up2));
    }

    [Fact]
    public void KeyBetweenTaps_IsNotCompose()
    {
        var detector = new ChordDetector();
        Tap(detector, LeftCtrl, 0, 100);
        Tap(detector, KeyT, 150, 180); // Ctrl, then typing

        Assert.Equal(ChordEvent.None, Tap(detector, LeftCtrl, 200, 300));
    }

    [Fact]
    public void CtrlShortcut_IsNotATap()
    {
        var detector = new ChordDetector();
        Tap(detector, LeftCtrl, 0, 100);
        detector.OnKey(LeftCtrl, true, Ms(200));
        detector.OnKey(KeyT, true, Ms(220)); // Ctrl+T

        Assert.Equal(ChordEvent.None, detector.OnKey(LeftCtrl, false, Ms(300)));
    }

    [Fact]
    public void DoubleTapCtrlWin_KeepsDictating_UntilTheNextChord()
    {
        var detector = new ChordDetector();
        var pressed = new ChordEvent(Chord.Dictate, ChordSignal.Pressed);
        var released = new ChordEvent(Chord.Dictate, ChordSignal.Released);

        Assert.Equal((pressed, released), ChordTap(detector, LeftCtrl, LeftWin, 0, 150));
        Assert.Equal((pressed, ChordEvent.None), ChordTap(detector, LeftCtrl, LeftWin, 400, 500)); // locked
        detector.OnKey(KeyT, true, Ms(2000)); // typing elsewhere doesn't stop it
        Assert.Equal((released, ChordEvent.None), ChordTap(detector, LeftCtrl, LeftWin, 5000, 5100));
        Assert.Equal((pressed, released), ChordTap(detector, LeftCtrl, LeftWin, 9000, 12000)); // holds work again
    }

    [Fact]
    public void SecondCtrlWinHold_IsNotHandsFree()
    {
        var detector = new ChordDetector();
        ChordTap(detector, LeftCtrl, LeftWin, 0, 150);

        Assert.Equal(new ChordEvent(Chord.Dictate, ChordSignal.Released), ChordTap(detector, LeftCtrl, LeftWin, 400, 2000).Up);
    }

    [Fact]
    public void Escape_EndsHandsFreeDictation()
    {
        var detector = new ChordDetector();
        ChordTap(detector, LeftCtrl, LeftWin, 0, 150);
        ChordTap(detector, LeftCtrl, LeftWin, 400, 500);

        Assert.Equal(new ChordEvent(Chord.None, ChordSignal.Cancelled), detector.OnKey(Escape, true, Ms(3000)));
        Assert.Equal(new ChordEvent(Chord.Dictate, ChordSignal.Pressed), ChordTap(detector, LeftCtrl, LeftWin, 5000, 6000).Down);
    }

    [Fact]
    public void RemappedChords_ReplaceTheDefaults()
    {
        var detector = new ChordDetector();
        detector.UseChords("Alt + Win", "Ctrl + Shift");

        Assert.Equal(ChordEvent.None, ChordTap(detector, LeftCtrl, LeftAlt, 0, 1000).Down);
        Assert.Equal(new ChordEvent(Chord.Talk, ChordSignal.Pressed), ChordTap(detector, LeftAlt, LeftWin, 2000, 3000).Down);
        Assert.Equal(new ChordEvent(Chord.Dictate, ChordSignal.Pressed), ChordTap(detector, LeftCtrl, LeftShift, 4000, 5000).Down);
    }

    [Theory]
    [InlineData("Ctrl + Win", "Ctrl + Win")] // the same chord twice
    [InlineData("Ctrl", "Ctrl + Win")] // one key
    [InlineData("Ctrl + Banana", "Ctrl + Win")]
    public void InvalidChords_KeepTheCurrentOnes(string talk, string dictate)
    {
        var detector = new ChordDetector();
        detector.UseChords(talk, dictate);

        Assert.Equal(new ChordEvent(Chord.Talk, ChordSignal.Pressed), ChordTap(detector, LeftCtrl, LeftAlt, 0, 1000).Down);
    }

    /// <summary>Presses two modifiers together, then lets the second go first.</summary>
    private static (ChordEvent Down, ChordEvent Up) ChordTap(ChordDetector detector, int first, int second, int downMs, int upMs)
    {
        detector.OnKey(first, true, Ms(downMs));
        var down = detector.OnKey(second, true, Ms(downMs + 10));
        var up = detector.OnKey(second, false, Ms(upMs));
        detector.OnKey(first, false, Ms(upMs + 10));
        return (down, up);
    }

    private static ChordEvent Tap(ChordDetector detector, int key, int downMs, int upMs)
    {
        detector.OnKey(key, true, Ms(downMs));
        return detector.OnKey(key, false, Ms(upMs));
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);
}
