using Jarvis.Contracts.Vault;
using Jarvis.Core.Agents;
using Jarvis.Core.Companion;
using Jarvis.Core.Vault;

namespace Jarvis.Tests.Vault;

public sealed class ProfileNotesTests
{
    private const string Traits = "# What JARVIS knows about you\n\nIntro line.\n\n- Works late — yes: “Spot on”\n- Learns from videos\n";

    [Fact]
    public void Facts_AreTheBulletLines()
        => Assert.Equal(["Works late — yes: “Spot on”", "Learns from videos"], ProfileNotes.Facts(Traits));

    [Fact]
    public void Replace_ChangesOnlyThatLine_AndKeepsTheRest()
    {
        var edited = ProfileNotes.Replace(Traits, "Learns from videos", "Learns from videos and podcasts")!;
        Assert.Contains("- Learns from videos and podcasts", edited, StringComparison.Ordinal);
        Assert.Contains("Intro line.", edited, StringComparison.Ordinal);
        Assert.Contains("- Works late", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_WithNothing_DeletesTheLine()
        => Assert.DoesNotContain("videos", ProfileNotes.Replace(Traits, "Learns from videos", "  ")!, StringComparison.Ordinal);

    [Fact]
    public void Replace_ALineThatChangedElsewhere_IsNull_SoTheUserReloads()
        => Assert.Null(ProfileNotes.Replace(Traits, "Not in the file", "x"));

    [Fact]
    public void Replace_KeepsWhatTheBrainAppendedMeanwhile()
    {
        var meanwhile = Traits + "- Hates meetings — yes\n"; // a hunch answered on another device
        Assert.Contains("Hates meetings", ProfileNotes.Replace(meanwhile, "Learns from videos", "Learns by doing")!, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_StartsANewFileWithAHeading_AndKeepsFactsOnOneLine()
    {
        Assert.Equal("# What Jarvis knows about you\n\n- Likes chess\n", ProfileNotes.Add(null, "Likes chess", ProfileNotes.Traits));
        Assert.EndsWith("- Two lines become one\n", ProfileNotes.Add(Traits, "Two lines\nbecome one", ProfileNotes.Traits), StringComparison.Ordinal);
    }

    [Fact]
    public void Files_ShowTraitsFirst_AndHideTheBrainsOwnView()
    {
        VaultEntry File(string name) => new($"Profile/{name}", name, IsFolder: false, Modified: null);
        var files = ProfileNotes.Files([File("work.md"), File("hunches.md"), File("not-me.md"), File("traits.md"), File("form-memory.json"), new("Profile/forms", "forms", true, null)]);
        Assert.Equal(["Profile/traits.md", "Profile/work.md", "Profile/not-me.md"], files);
    }

    [Fact]
    public void Chat_IsStillThereAfterARestart_UntilForgotten()
    {
        var file = Path.Combine(Path.GetTempPath(), $"jarvis-chat-{Guid.NewGuid():N}.json");
        try
        {
            new ConversationLog(file).Add("What's on today?", "Two meetings.");
            var reopened = new ConversationLog(file);
            Assert.Equal("Two meetings.", Assert.Single(reopened.Recent).Answer);

            reopened.Clear();
            Assert.Empty(new ConversationLog(file).Recent);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Chat_FromADamagedFile_StartsEmpty()
    {
        var file = Path.Combine(Path.GetTempPath(), $"jarvis-chat-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, "{ not json");
        try
        {
            Assert.Empty(new ConversationLog(file).Recent);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void JobCards_ComeBackAfterARestart()
    {
        var jobs = Path.Combine(Path.GetTempPath(), $"jarvis-jobs-{Guid.NewGuid():N}");
        try
        {
            var done = Directory.CreateDirectory(Path.Combine(jobs, "20261123-091500-trip-plan")).FullName;
            File.WriteAllText(Path.Combine(done, "brief.md"), "Plan a weekend trip to Goa");
            File.WriteAllText(Path.Combine(done, "result.md"), "Here's the plan.");
            var stopped = Directory.CreateDirectory(Path.Combine(jobs, "20261123-101500-news")).FullName;
            File.WriteAllText(Path.Combine(stopped, "brief.md"), "Summarise the news");
            Directory.CreateDirectory(Path.Combine(jobs, "not-a-job"));

            var past = AgentJobManager.Past(jobs, 12);
            Assert.Equal(["Summarise the news", "Plan a weekend trip to Goa"], past.Select(job => job.Title));
            Assert.Equal(AgentJobStatus.Cancelled, past[0].Status); // Jarvis quit while it ran: Retry is offered
            Assert.Equal((AgentJobStatus.Finished, "Here's the plan."), (past[1].Status, past[1].Result));
            Assert.Equal(new DateTime(2026, 11, 23, 9, 15, 0), past[1].Started.DateTime);
        }
        finally
        {
            Directory.Delete(jobs, recursive: true);
        }
    }
}
