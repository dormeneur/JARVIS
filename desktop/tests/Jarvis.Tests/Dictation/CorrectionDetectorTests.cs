using Jarvis.Core.Dictation;

namespace Jarvis.Tests.Dictation;

public sealed class CorrectionDetectorTests
{
    private const string Inserted = "Can you send the deck to Pria before then?";
    private const string Field = "Hi team,\r" + Inserted;

    [Fact]
    public void RenamingADictatedName_IsLearned()
        => Assert.Equal(new Correction("Pria", "Priya"), CorrectionDetector.Find(Inserted, Field, Field.Replace("Pria", "Priya", StringComparison.Ordinal)));

    [Fact]
    public void FixingOneLetter_ReportsTheWholeWord()
        => Assert.Equal(new Correction("Pria", "Prya"), CorrectionDetector.Find(Inserted, Field, Field.Replace("Pria", "Prya", StringComparison.Ordinal)));

    [Fact]
    public void Acronyms_AreLearned()
    {
        const string Dictated = "ship the sdk today";

        Assert.Equal(new Correction("sdk", "SDK"), CorrectionDetector.Find(Dictated, Dictated, "ship the SDK today"));
    }

    [Theory]
    [InlineData("send teh deck", "send the deck")] // ordinary lowercase spelling fix
    [InlineData("send the deck", "send the deck. Thanks!")] // typing after our text
    [InlineData("send the deck to Pria", "send the slides to Priya")] // two words changed
    [InlineData("send it to Pria", "send it to pria")] // lowercased: nothing worth remembering
    public void OtherEdits_AreNotLearned(string dictated, string edited)
        => Assert.Null(CorrectionDetector.Find(dictated, dictated, edited));

    [Fact]
    public void EditsOutsideTheDictatedText_AreIgnored()
        => Assert.Null(CorrectionDetector.Find(Inserted, Field, "Hi Team,\r" + Inserted));

    [Fact]
    public void LineEndingsDifferingBetweenAppAndUs_StillMatch()
    {
        const string Dictated = "Thursday at three.\nSend it to Pria.";
        const string InNotepad = "Thursday at three.\rSend it to Pria.";

        Assert.Equal(new Correction("Pria", "Priya"), CorrectionDetector.Find(Dictated, InNotepad, "Thursday at three.\rSend it to Priya."));
    }

    [Fact]
    public void NoChange_IsNothing()
        => Assert.Null(CorrectionDetector.Find(Inserted, Field, Field));
}
