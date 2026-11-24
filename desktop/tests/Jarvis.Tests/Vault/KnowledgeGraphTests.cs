using System.Numerics;
using Jarvis.Core.Agents;
using Jarvis.Core.Vault;

namespace Jarvis.Tests.Vault;

public sealed class KnowledgeGraphTests
{
    private static readonly (string Path, string? Text)[] Files =
    [
        ("Profile/traits.md", "# x\n\n- Codes late at night — yes: “Spot on”\n- Learns new tools from videos\n"),
        ("Profile/interests.md", "- Watches coding videos on YouTube\n- Plays chess, see [[traits]]\n"),
    ];

    [Fact]
    public void Build_PutsYouInTheMiddle_TopicsAroundYou_FactsAroundTheirTopic()
    {
        var map = KnowledgeGraph.Build(Files);
        Assert.Equal(1 + 2 + 4, map.Nodes.Count);
        Assert.Contains(new GraphEdge(KnowledgeGraph.YouId, "Profile/traits.md", false), map.Edges);
        Assert.Contains(new GraphEdge("Profile/traits.md", "Profile/traits.md#0", false), map.Edges);
        Assert.Equal("Codes late at night", map.Nodes.Single(n => n.Id == "Profile/traits.md#0").Label); // hunch suffix trimmed
    }

    [Fact]
    public void Build_JoinsFactsThatShareWhatTheyAreAbout_AndFollowsLinks()
    {
        var map = KnowledgeGraph.Build(Files);
        Assert.Contains(map.Edges, e => e.Related && new[] { e.From, e.To }.Order().SequenceEqual(new[] { "Profile/interests.md#0", "Profile/traits.md#1" }.Order()));
        Assert.Contains(new GraphEdge("Profile/interests.md#1", "Profile/traits.md", true), map.Edges);
    }

    [Fact]
    public void Layout_SettlesWithoutBlowingUp_AndKeepsLinkedNodesCloser()
    {
        var layout = new ForceLayout(KnowledgeGraph.Build(Files));
        var first = layout.Step();
        float last = first;
        for (var i = 0; i < 600; i++)
        {
            last = layout.Step();
        }

        Assert.All(layout.Positions, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y)));
        Assert.True(last < 0.05f, $"still moving: {last}");
        var you = layout.Positions[0];
        var topic = layout.Positions[1];
        var farFact = layout.Positions[^1];
        Assert.True(Vector2.Distance(topic, layout.Positions[2]) < Vector2.Distance(you, farFact) + 200);
    }

    [Fact]
    public void Layout_KeepsADraggedNodeWhereThePointerIs()
    {
        var layout = new ForceLayout(KnowledgeGraph.Build(Files)) { Pinned = 2 };
        layout.Positions[2] = new Vector2(300, 300);
        layout.Step();
        Assert.Equal(new Vector2(300, 300), layout.Positions[2]);
    }

    [Theory]
    [InlineData("Travel", "Profile/travel.md")]
    [InlineData("people/Family Members", "Profile/people/family-members.md")]
    [InlineData("  ", null)]
    [InlineData("a/b/c", null)]
    [InlineData("forms/new", null)]
    [InlineData("../../Secrets", "Profile/secrets.md")] // traversal collapses to a plain name inside Profile
    public void TopicPath_IsAlwaysInsideProfile(string name, string? expected) => Assert.Equal(expected, ProfileNotes.TopicPath(name));

    [Fact]
    public void IsKnowledge_ShowsFactsAndNewTopics_NotPagesOfParagraphs()
    {
        Assert.True(ProfileNotes.IsKnowledge("Profile/travel.md", "# Travel\n\n"));
        Assert.True(ProfileNotes.IsKnowledge("Profile/work.md", "- Builds JARVIS"));
        Assert.False(ProfileNotes.IsKnowledge("Profile/standard-answers.md", "## Why us?\nBecause I love it."));
    }

    [Fact]
    public void DismissedJobs_StayGoneAfterARestart()
    {
        var jobs = Path.Combine(Path.GetTempPath(), $"jarvis-jobs-{Guid.NewGuid():N}");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(jobs, "20261123-091500-x")).FullName;
            File.WriteAllText(Path.Combine(folder, "brief.md"), "Do x");
            Assert.Single(AgentJobManager.Past(jobs, 12));
            File.WriteAllText(Path.Combine(folder, AgentJobManager.DismissedMarker), string.Empty);
            Assert.Empty(AgentJobManager.Past(jobs, 12));
        }
        finally
        {
            Directory.Delete(jobs, recursive: true);
        }
    }

    [Fact]
    public void FolderTopics_HangOffAFolderNode()
    {
        var map = KnowledgeGraph.Build([("Profile/people/family.md", "- Sister Priya")]);
        Assert.Contains(map.Nodes, n => n.Kind == GraphNodeKind.Folder && n.Path == "Profile/people" && n.Label == "People");
        Assert.Contains(new GraphEdge(KnowledgeGraph.YouId, "Profile/people/", false), map.Edges);
        Assert.Contains(new GraphEdge("Profile/people/", "Profile/people/family.md", false), map.Edges);
    }

    [Fact]
    public void AnEdit_KeepsWhatWasThere_AndGrowsTheNewFactBesideItsTopic()
    {
        var before = new ForceLayout(KnowledgeGraph.Build(Files));
        for (var i = 0; i < 300; i++)
        {
            before.Step();
        }

        var snapshot = before.Snapshot();
        var grown = new (string, string?)[] { Files[0], (Files[1].Path, Files[1].Text + "- Reads sci-fi\n") };
        var after = new ForceLayout(KnowledgeGraph.Build(grown), snapshot);
        var ids = after.Nodes.Select(n => n.Id).ToList();
        Assert.Equal(snapshot["Profile/traits.md#0"], after.Positions[ids.IndexOf("Profile/traits.md#0")]);
        var topic = after.Positions[ids.IndexOf("Profile/interests.md")];
        Assert.True(Vector2.Distance(topic, after.Positions[ids.IndexOf("Profile/interests.md#2")]) < 25);

        float biggest = 0;
        for (var i = 0; i < 60; i++)
        {
            var old = after.Positions.ToArray();
            after.Step();
            biggest = Math.Max(biggest, after.Positions.Zip(old, Vector2.Distance).Max());
        }

        Assert.True(biggest <= 9.01f, $"a node jumped {biggest} in one frame"); // calm, not a reshuffle
    }

    [Theory]
    [InlineData("chess", true)]
    [InlineData("CHESS plays", true)]
    [InlineData("chess football", false)]
    [InlineData("", false)]
    public void Search_MatchesEveryWordTyped(string query, bool expected)
        => Assert.Equal(expected, KnowledgeGraph.Matches(new GraphNode("x", GraphNodeKind.Fact, "Plays chess", "Plays chess, see [[traits]]", "Profile/interests.md", 1), query));
}
