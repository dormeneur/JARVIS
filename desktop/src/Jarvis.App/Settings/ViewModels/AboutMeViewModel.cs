using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jarvis.Contracts.Vault;
using Jarvis.Core.Vault;

namespace Jarvis.App.Settings.ViewModels;

/// <summary>
/// "About me": what Jarvis has learned about the user, read from the vault's Profile/ files and
/// edited in place. Every change goes through jv-api (versioned, so phones sync it) and replaces only
/// the line it was made on (ProfileNotes), so nothing the brain added meanwhile is lost.
/// </summary>
public sealed partial class AboutMeViewModel : ObservableObject
{
    private readonly IVault _vault;

    public AboutMeViewModel(IVault vault)
    {
        _vault = vault;
        _ = ReloadAsync();
    }

    /// <summary>What the list shows: every group, or only what matches <see cref="Query"/>.</summary>
    public ObservableCollection<ProfileGroup> Groups { get; } = [];

    private readonly List<ProfileGroup> _all = [];

    /// <summary>Search: the list keeps matching facts (a topic whose name matches keeps all of its own); the graph lights them up.</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    partial void OnQueryChanged(string value) => ApplyQuery();

    private void ApplyQuery()
    {
        Groups.Clear();
        var words = Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool Has(string text) => words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        foreach (var group in _all)
        {
            if (words.Length == 0 || Has(group.Title) || Has(group.Path))
            {
                Groups.Add(group);
            }
            else if (group.Facts.Where(row => Has(row.Text)).ToList() is { Count: > 0 } found)
            {
                Groups.Add(new ProfileGroup(group.Path, group.Title, found)); // the same rows, so edits still save
            }
        }
    }

    /// <summary>The facts of one Profile file (for the graph's side panel), editable like the list's.</summary>
    public IReadOnlyList<FactRow> FactsOf(string path) => _all.FirstOrDefault(group => group.Path == path)?.Facts ?? [];

    /// <summary>Renames a topic (moves its file): "Hobbies", or "people/friends" to move it into a folder.</summary>
    public async Task RenameTopicAsync(string path, string name)
    {
        var target = ProfileNotes.TopicPath(name.Contains('/', StringComparison.Ordinal) || KnowledgeGraph.FolderOf(path) is not { } folder ? name : $"{folder[(ProfileNotes.Folder.Length + 1)..]}/{name}");
        if (target is null || target == path)
        {
            Status = "Give it a new name, like Hobbies, or people/friends to put it in a folder.";
            return;
        }

        try
        {
            if (await _vault.ReadFileAsync(target, CancellationToken.None).ConfigureAwait(true) is not null)
            {
                Status = $"There's already a {ProfileNotes.Title(target)}.";
                return;
            }

            var text = await _vault.ReadFileAsync(path, CancellationToken.None).ConfigureAwait(true) ?? string.Empty;
            var lines = text.Split('\n').ToList();
            if (lines.Count > 0 && lines[0].StartsWith("# ", StringComparison.Ordinal))
            {
                lines[0] = $"# {ProfileNotes.Title(target)}"; // the heading follows the new name
            }

            await _vault.WriteFileAsync(target, string.Join('\n', lines), CancellationToken.None).ConfigureAwait(true);
            await _vault.DeleteAsync(path, CancellationToken.None).ConfigureAwait(true);
            Selected = null;
            await ReloadAsync().ConfigureAwait(true);
            Status = $"Renamed to {ProfileNotes.Title(target)} ({target}).";
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Couldn't rename it: {exception.Message}";
        }
    }

    /// <summary>Deletes a topic (its file) or a folder (with every topic in it). The server keeps versions; your phone syncs the change.</summary>
    public async Task DeleteAsync(string path, bool folder)
    {
        try
        {
            await _vault.DeleteAsync(path, CancellationToken.None).ConfigureAwait(true);
            Selected = null;
            await ReloadAsync().ConfigureAwait(true);
            Status = $"Deleted {(folder ? "the folder " : string.Empty)}{path}.";
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Couldn't delete it: {exception.Message}";
        }
    }

    /// <summary>Opens a Profile file (selected) or folder in File Explorer, when the vault is on this PC.</summary>
    public async Task ShowInExplorerAsync(string path, bool folder)
    {
        string? root = null;
        try
        {
            root = await _vault.LocalFolderAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Couldn't ask the vault where it is: {exception.Message}";
            return;
        }

        if (root is null)
        {
            Status = "Your vault folder isn't on this PC (or the server didn't say where). Set Vault:Folder in settings.json to open it here.";
            return;
        }

        var full = System.IO.Path.Combine(root, (path.Length == 0 ? ProfileNotes.Folder : path).Replace('/', System.IO.Path.DirectorySeparatorChar));
        var arguments = folder || !System.IO.File.Exists(full) ? $"\"{(System.IO.Directory.Exists(full) ? full : root)}\"" : $"/select,\"{full}\"";
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "Loading…";

    /// <summary>The same knowledge as a graph (rebuilt on every load).</summary>
    [ObservableProperty]
    public partial KnowledgeMap Map { get; set; } = new([], []);

    /// <summary>The fact or topic the user picked on the graph (null: nothing picked).</summary>
    [ObservableProperty]
    public partial GraphNode? Selected { get; set; }

    /// <summary>"Tell Jarvis something about you": filed wherever it belongs.</summary>
    [ObservableProperty]
    public partial string Anything { get; set; } = string.Empty;

    /// <summary>Saves a fact where the local brain says it belongs (an existing file, or a new topic or folder).</summary>
    [RelayCommand]
    private async Task AddAnywhereAsync()
    {
        var fact = Anything.Trim();
        if (fact.Length == 0 || !_vault.IsConnected)
        {
            return;
        }

        Status = "Finding where that belongs…";
        try
        {
            var path = await _vault.PlaceFactAsync(fact, CancellationToken.None).ConfigureAwait(true) ?? ProfileNotes.Traits;
            if (await ChangeAsync(path, file => ProfileNotes.Add(file, fact, path)).ConfigureAwait(true))
            {
                Anything = string.Empty;
                await ReloadAsync().ConfigureAwait(true);
                Status = $"Filed under {ProfileNotes.Title(path)} ({path}).";
            }
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            Status = exception.Message;
        }
    }

    /// <summary>A new, empty topic: "Travel" or "people/friends" (a folder too). Facts go in from the page.</summary>
    public async Task AddTopicAsync(string name)
    {
        if (ProfileNotes.TopicPath(name) is not { } path)
        {
            Status = "Give the topic a name, like Travel, or people/friends for a folder.";
            return;
        }

        try
        {
            if (await _vault.ReadFileAsync(path, CancellationToken.None).ConfigureAwait(true) is not null)
            {
                Status = $"{ProfileNotes.Title(path)} is already there.";
                return;
            }

            await _vault.WriteFileAsync(path, $"# {ProfileNotes.Title(path)}\n\n", CancellationToken.None).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            Status = $"Made {path}. Add things to it.";
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Couldn't make it: {exception.Message}";
        }
    }

    /// <summary>The graph's picked fact, edited (an empty text deletes it).</summary>
    public async Task SaveSelectedAsync(string text)
    {
        if (Selected is not { Kind: GraphNodeKind.Fact } node)
        {
            return;
        }

        if (await ChangeAsync(node.Path, file => ProfileNotes.Replace(file, node.Text, text)).ConfigureAwait(true))
        {
            Selected = null;
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>A fact added to the topic picked on the graph.</summary>
    public async Task AddToSelectedAsync(string fact)
    {
        if (Selected is not { Kind: GraphNodeKind.Topic } topic || fact.Trim().Length == 0)
        {
            return;
        }

        if (await ChangeAsync(topic.Path, file => ProfileNotes.Add(file, fact, topic.Path)).ConfigureAwait(true))
        {
            await ReloadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (!_vault.IsConnected)
        {
            Groups.Clear();
            Status = "Connect Jarvis to your vault first (Settings → Vault).";
            return;
        }

        try
        {
            // Profile/ and one folder down (Profile/people/family.md), not other features' folders.
            var entries = (await _vault.ListAsync(ProfileNotes.Folder, CancellationToken.None).ConfigureAwait(true)).ToList();
            foreach (var folder in entries.Where(entry => entry.IsFolder && !ProfileNotes.OtherFolders.Contains(entry.Name) && !entry.Name.StartsWith('.')).ToList())
            {
                entries.AddRange(await _vault.ListAsync(folder.Path, CancellationToken.None).ConfigureAwait(true));
            }

            var groups = new List<ProfileGroup>();
            var texts = new List<(string Path, string? Text)>();
            foreach (var path in ProfileNotes.Files(entries))
            {
                var text = await _vault.ReadFileAsync(path, CancellationToken.None).ConfigureAwait(true);
                texts.Add((path, text));
                var facts = ProfileNotes.Facts(text);
                // Pages of paragraphs (standard answers) aren't shown; traits and new topics always are, to add to.
                if (ProfileNotes.IsKnowledge(path, text))
                {
                    groups.Add(new ProfileGroup(path, ProfileNotes.Title(path), [.. facts.Select(fact => new FactRow(path, fact))]));
                }
            }

            if (groups.All(group => group.Path != ProfileNotes.Traits))
            {
                groups.Insert(0, new ProfileGroup(ProfileNotes.Traits, ProfileNotes.Title(ProfileNotes.Traits), []));
            }

            _all.Clear();
            _all.AddRange(groups);
            ApplyQuery();

            Map = KnowledgeGraph.Build(texts.Where(file => ProfileNotes.IsKnowledge(file.Path, file.Text)).ToList());

            var count = groups.Sum(group => group.Facts.Count);
            Status = count == 0
                ? "Nothing yet. Answer a few hunches (the notch asks now and then, or open Hunches on its home) and what Jarvis learns shows up here."
                : $"{count} things Jarvis knows about you. Edit a line and press Enter, or delete what's wrong.";
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Couldn't read your profile: {exception.Message}";
        }
    }

    /// <summary>Saves an edited fact (an emptied one is deleted).</summary>
    public async Task SaveAsync(FactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Text.Trim() == row.Original)
        {
            return;
        }

        if (await ChangeAsync(row.Path, file => ProfileNotes.Replace(file, row.Original, row.Text)).ConfigureAwait(true))
        {
            if (row.Text.Trim().Length == 0)
            {
                Remove(row);
            }
            else
            {
                row.Original = row.Text.Trim();
            }
        }
    }

    public async Task DeleteAsync(FactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (await ChangeAsync(row.Path, file => ProfileNotes.Replace(file, row.Original, null)).ConfigureAwait(true))
        {
            Remove(row);
        }
    }

    public async Task AddAsync(ProfileGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var fact = group.NewFact.Trim();
        if (fact.Length == 0)
        {
            return;
        }

        if (await ChangeAsync(group.Path, file => ProfileNotes.Add(file, fact, group.Path)).ConfigureAwait(true))
        {
            group.Facts.Add(new FactRow(group.Path, fact));
            group.NewFact = string.Empty;
        }
    }

    /// <summary>Reads the file as it is now, applies the change, writes it back. False (and a fresh view) if the line moved on elsewhere.</summary>
    private async Task<bool> ChangeAsync(string path, Func<string?, string?> change)
    {
        try
        {
            var updated = change(await _vault.ReadFileAsync(path, CancellationToken.None).ConfigureAwait(true));
            if (updated is null)
            {
                await ReloadAsync().ConfigureAwait(true);
                Status = "That line was changed somewhere else, so here's the latest. Try again.";
                return false;
            }

            await _vault.WriteFileAsync(path, updated, CancellationToken.None).ConfigureAwait(true);
            Status = $"Saved to {path}.";
            return true;
        }
        catch (Exception exception) when (exception is VaultException or HttpRequestException or TaskCanceledException)
        {
            Status = $"Couldn't save: {exception.Message}";
            return false;
        }
    }

    private void Remove(FactRow row)
    {
        foreach (var group in Groups.Concat(_all))
        {
            group.Facts.Remove(row);
        }
    }
}

/// <summary>One Profile file on the page.</summary>
public sealed partial class ProfileGroup(string path, string title, IEnumerable<FactRow> facts) : ObservableObject
{
    public string Path { get; } = path;

    public string Title { get; } = title;

    public ObservableCollection<FactRow> Facts { get; } = [.. facts];

    [ObservableProperty]
    public partial string NewFact { get; set; } = string.Empty;
}

/// <summary>One fact, as it is in the file (<see cref="Original"/>) and as the user is editing it.</summary>
public sealed partial class FactRow : ObservableObject
{
    public FactRow(string path, string fact)
    {
        Path = path;
        Original = fact;
        Text = fact;
    }

    public string Path { get; }

    public string Original { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; }
}
