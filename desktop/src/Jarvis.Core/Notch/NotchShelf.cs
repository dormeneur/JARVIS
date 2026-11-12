using System.Text.Json;

namespace Jarvis.Core.Notch;

/// <summary>
/// Files dropped on the notch, held there to drag out later. Only the paths are kept (in a JSON
/// file), newest first; a file moved or deleted since simply drops off the list. Thread-safe.
/// </summary>
public sealed class NotchShelf(string file)
{
    public const int Capacity = 24;

    private readonly Lock _gate = new();
    private List<string>? _paths;

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate)
            {
                var paths = Load();
                if (paths.RemoveAll(path => !File.Exists(path) && !Directory.Exists(path)) > 0)
                {
                    Save(paths);
                }

                return [.. paths];
            }
        }
    }

    /// <summary>Adds what exists of <paramref name="paths"/> at the front (a file already there moves up).</summary>
    public void Add(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            var list = Load();
            foreach (var path in paths.Where(path => Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path))).Reverse())
            {
                list.Remove(path);
                list.Insert(0, path);
            }

            if (list.Count > Capacity)
            {
                list.RemoveRange(Capacity, list.Count - Capacity);
            }

            Save(list);
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            var list = Load();
            if (list.Remove(path))
            {
                Save(list);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Save([]);
        }
    }

    private List<string> Load()
    {
        if (_paths is null)
        {
            try
            {
                _paths = File.Exists(file) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [] : [];
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _paths = []; // a damaged list only costs the shelf, never the files on it
            }
        }

        return _paths;
    }

    private void Save(List<string> paths)
    {
        _paths = paths;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(paths));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this run; the next save tries again.
        }
    }
}
