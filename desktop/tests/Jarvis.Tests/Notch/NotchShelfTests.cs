using Jarvis.Core.Notch;

namespace Jarvis.Tests.Notch;

public sealed class NotchShelfTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("shelf").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Add_KeepsNewestFirst_SkipsMissingAndRelative_Persists()
    {
        var (a, b) = (Touch("a.txt"), Touch("b.txt"));
        var store = Path.Combine(_folder, "state", "shelf.json");
        var shelf = new NotchShelf(store);

        shelf.Add([a]);
        shelf.Add([b, Path.Combine(_folder, "gone.txt"), "relative.txt", a]);

        Assert.Equal([b, a], shelf.Items);
        Assert.Equal([b, a], new NotchShelf(store).Items);
    }

    [Fact]
    public void Items_DropFilesMovedAway_RemoveAndClearWork()
    {
        var (a, b, c) = (Touch("a.txt"), Touch("b.txt"), Touch("c.txt"));
        var shelf = new NotchShelf(Path.Combine(_folder, "shelf.json"));
        shelf.Add([a, b, c]);

        File.Delete(b);
        shelf.Remove(c);

        Assert.Equal([a], shelf.Items);
        shelf.Clear();
        Assert.Empty(shelf.Items);
    }

    [Fact]
    public void DamagedFile_IsAnEmptyShelf()
    {
        var store = Path.Combine(_folder, "shelf.json");
        File.WriteAllText(store, "{not json");

        Assert.Empty(new NotchShelf(store).Items);
    }
}
