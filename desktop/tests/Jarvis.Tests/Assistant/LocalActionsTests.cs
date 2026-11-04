using Jarvis.Core.Assistant;

namespace Jarvis.Tests.Assistant;

public sealed class LocalActionsTests
{
    private readonly FakeApps _apps = new(
        new("Spotify", @"C:\Apps\Spotify.exe"),
        new("Visual Studio Code", "Microsoft.VisualStudioCode"),
        new("Visual Studio Installer", "vsinstaller"),
        new("Calculator", "Microsoft.WindowsCalculator!App"),
        new("Settings", "windows.immersivecontrolpanel!microsoft.windows.immersivecontrolpanel"),
        new("WhatsApp", "WhatsAppDesktop!App"),
        new("Microsoft Teams", "MSTeams!App"));

    [Theory]
    [InlineData("open spotify", "Spotify")]
    [InlineData("Hey Jarvis, can you launch VS Code for me?", "Visual Studio Code")]
    [InlineData("please open the calculator app", "Calculator")]
    [InlineData("start teams", "Microsoft Teams")]
    [InlineData("open settings", "Settings")]
    [InlineData("Open WhatsApp.", "WhatsApp")]
    public void OpensInstalledAppsByWhatPeopleCallThem(string request, string app)
    {
        var done = new LocalActions(_apps).TryHandle(request);

        Assert.NotNull(done);
        Assert.Equal([app], _apps.Launched.Select(launched => launched.Name));
    }

    [Theory]
    [InlineData("open bluetooth settings", "ms-settings:bluetooth")]
    [InlineData("open wifi", "ms-settings:network-wifi")]
    [InlineData("open my downloads", "shell:Downloads")]
    [InlineData("open youtube", "https://www.youtube.com")]
    [InlineData("go to github.com/dotnet", "https://github.com/dotnet")]
    [InlineData("search for lofi beats on youtube", "https://www.youtube.com/results?search_query=lofi%20beats")]
    [InlineData("google the weather in chennai", "https://www.google.com/search?q=the%20weather%20in%20chennai")]
    [InlineData("play taylor swift on spotify", "spotify:search:taylor%20swift")]
    public void OpensSettingsFoldersSitesAndSearchesByLink(string request, string uri)
    {
        Assert.NotNull(new LocalActions(_apps).TryHandle(request));
        Assert.Equal([uri], _apps.Opened);
    }

    [Theory]
    [InlineData("where is the share button")]
    [InlineData("open the file I was working on yesterday")]
    [InlineData("start a timer for ten minutes")]
    [InlineData("what's on my calendar tomorrow")]
    public void LeavesEverythingElseToTheBrain(string request)
    {
        Assert.Null(new LocalActions(_apps).TryHandle(request));
        Assert.Empty(_apps.Launched);
        Assert.Empty(_apps.Opened);
    }

    [Fact]
    public void SuggestsNextStepsAsDeepLinks()
    {
        var done = new LocalActions(_apps).TryHandle("open spotify")!;

        Assert.Contains(done.Next, next => next.Label == "Liked songs" && next.Uri == "spotify:collection:tracks");
        Assert.Empty(new LocalActions(_apps).TryHandle("open calculator")!.Next);
    }

    [Fact]
    public async Task DropTheNeedle_TurnsTheVolumeUp_OpensLikedSongs_AndPresses_Play()
    {
        var done = await new LocalActions(_apps, new Instant()).RunAsync("drop the needle", [], CancellationToken.None);

        Assert.Equal("Dropping the needle: your liked songs on Spotify.", done!.Said);
        Assert.Equal(["volume 60", "open spotify:collection:tracks", "key playpause"], _apps.Steps);
    }

    [Fact]
    public async Task ATaughtWorkflowRuns_AndAPassingMentionDoesnt()
    {
        var taught = new Workflow(["good morning"], [new("do", "open spotify"), new("open", "https://mail.google.com")], "Morning.");
        var actions = new LocalActions(_apps, new Instant());

        Assert.Equal("Morning.", (await actions.RunAsync("Good morning, Jarvis!", [taught], CancellationToken.None))!.Said);
        Assert.Equal(["Spotify"], _apps.Launched.Select(app => app.Name));
        Assert.Contains("open https://mail.google.com", _apps.Steps);

        _apps.Steps.Clear();
        Assert.Null(await actions.RunAsync("why do DJs say drop the needle when they start a song", [], CancellationToken.None));
        Assert.Empty(_apps.Steps);
    }

    [Fact]
    public void LearnsWhatAPhraseMeans_FromHowItsTaught()
    {
        Assert.Equal(("drop the needle", "open spotify, go to liked songs and hit play"), Workflows.Teaching("When I say drop the needle, open spotify, go to liked songs and hit play"));
        Assert.Equal(("focus time", "close slack and play lofi"), Workflows.Teaching("whenever I say \"focus time\" it means close slack and play lofi"));
        Assert.Null(Workflows.Teaching("open spotify"));

        var steps = Workflows.ParseSteps("""Here: [{"do":"open","value":"spotify:collection:tracks"},{"do":"wait","value":"3000"},{"do":"key","value":"playpause"}]""");
        Assert.Equal(3, steps!.Count);
        Assert.Null(Workflows.ParseSteps("""[{"do":"run","value":"format c:"}]""")); // only the known kinds
    }

    private sealed class Instant : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private sealed class FakeApps(params InstalledApp[] apps) : IApps
    {
        public List<InstalledApp> Launched { get; } = [];

        public List<string> Opened { get; } = [];

        public IReadOnlyList<InstalledApp> All => apps;

        public List<string> Steps { get; } = [];

        public bool PressKey(string key)
        {
            Steps.Add("key " + key);
            return true;
        }

        public void EnsureVolume(int percent) => Steps.Add("volume " + percent);

        public bool Launch(InstalledApp app)
        {
            Launched.Add(app);
            return true;
        }

        public bool Open(string uri)
        {
            Opened.Add(uri);
            Steps.Add("open " + uri);
            return true;
        }
    }
}
