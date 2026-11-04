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

    private sealed class FakeApps(params InstalledApp[] apps) : IApps
    {
        public List<InstalledApp> Launched { get; } = [];

        public List<string> Opened { get; } = [];

        public IReadOnlyList<InstalledApp> All => apps;

        public bool Launch(InstalledApp app)
        {
            Launched.Add(app);
            return true;
        }

        public bool Open(string uri)
        {
            Opened.Add(uri);
            return true;
        }
    }
}
