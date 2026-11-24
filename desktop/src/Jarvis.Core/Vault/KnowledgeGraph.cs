using System.Numerics;
using System.Text.RegularExpressions;

namespace Jarvis.Core.Vault;

public enum GraphNodeKind
{
    You,
    Folder,
    Topic,
    Fact,
}

/// <param name="Text">The fact as it is in the file (for editing); the topic's or the user's name otherwise.</param>
/// <param name="Group">Its topic's index, for its colour (−1 for You).</param>
public sealed record GraphNode(string Id, GraphNodeKind Kind, string Label, string Text, string Path, int Group);

/// <param name="Related">Two facts that share what they're about (drawn fainter than structure).</param>
public sealed record GraphEdge(string From, string To, bool Related);

public sealed record KnowledgeMap(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// What Jarvis knows about the user as a graph, like Obsidian's: the user in the middle, each
/// Profile file a topic around them, each fact around its topic, and facts that share what they're
/// about (meaningful words in common, or a [[link]] to a topic) joined across topics.
/// </summary>
public static partial class KnowledgeGraph
{
    public const string YouId = "you";
    private const int RelatedPerFact = 3;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "this", "with", "from", "have", "your", "they", "them", "their", "what", "when", "where", "which",
        "would", "could", "should", "about", "there", "been", "were", "just", "like", "more", "most", "some",
        "only", "also", "very", "into", "over", "than", "then", "said", "says", "noticed", "partly", "words",
        "spot", "yes", "really", "always", "often", "usually", "every", "much", "many", "make", "does",
    };

    public static KnowledgeMap Build(IReadOnlyList<(string Path, string? Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var nodes = new List<GraphNode> { new(YouId, GraphNodeKind.You, "You", "You", string.Empty, -1) };
        var edges = new List<GraphEdge>();
        var facts = new List<(GraphNode Node, HashSet<string> Words)>();
        var topics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // file name → topic id
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // folder path -> node id
        for (var group = 0; group < files.Count; group++)
        {
            var (path, text) = files[group];
            var topic = new GraphNode(path, GraphNodeKind.Topic, ProfileNotes.Title(path), ProfileNotes.Title(path), path, group);
            nodes.Add(topic);
            // Profile/people/family.md hangs off a "People" folder node, which hangs off you.
            var parent = YouId;
            if (FolderOf(path) is { } folder)
            {
                if (!folders.TryGetValue(folder, out var folderId))
                {
                    folderId = folder + "/";
                    folders[folder] = folderId;
                    var name = folder[(folder.LastIndexOf('/') + 1)..];
                    nodes.Add(new GraphNode(folderId, GraphNodeKind.Folder, char.ToUpperInvariant(name[0]) + name[1..].Replace('-', ' '), name, folder, -2));
                    edges.Add(new GraphEdge(YouId, folderId, Related: false));
                }

                parent = folderId;
            }

            edges.Add(new GraphEdge(parent, topic.Id, Related: false));
            topics[System.IO.Path.GetFileNameWithoutExtension(path)] = topic.Id;
            var index = 0;
            foreach (var fact in ProfileNotes.Facts(text))
            {
                var node = new GraphNode($"{path}#{index++}", GraphNodeKind.Fact, Short(fact), fact, path, group);
                nodes.Add(node);
                edges.Add(new GraphEdge(topic.Id, node.Id, Related: false));
                facts.Add((node, Words(fact)));
            }
        }

        // [[links]] from a fact to a topic.
        foreach (var (node, _) in facts)
        {
            foreach (Match link in WikiLink().Matches(node.Text))
            {
                if (topics.TryGetValue(link.Groups[1].Value.Trim(), out var target) && target != node.Path)
                {
                    edges.Add(new GraphEdge(node.Id, target, Related: true));
                }
            }
        }

        // Facts that share what they're about, the strongest few per fact.
        var related = new HashSet<(string, string)>();
        for (var i = 0; i < facts.Count; i++)
        {
            var best = Enumerable.Range(0, facts.Count)
                .Where(j => j != i)
                .Select(j => (j, Shared: facts[i].Words.Intersect(facts[j].Words, StringComparer.OrdinalIgnoreCase).Count()))
                .Where(pair => pair.Shared >= 2 || (pair.Shared == 1 && facts[i].Node.Path != facts[pair.j].Node.Path))
                .OrderByDescending(pair => pair.Shared)
                .Take(RelatedPerFact);
            foreach (var (j, _) in best)
            {
                var key = i < j ? (facts[i].Node.Id, facts[j].Node.Id) : (facts[j].Node.Id, facts[i].Node.Id);
                if (related.Add(key))
                {
                    edges.Add(new GraphEdge(key.Item1, key.Item2, Related: true));
                }
            }
        }

        return new KnowledgeMap(nodes, edges);
    }

    /// <summary>"Profile/people" for "Profile/people/family.md"; null for a file straight in Profile/.</summary>
    public static string? FolderOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var cut = path.LastIndexOf('/');
        return cut > ProfileNotes.Folder.Length ? path[..cut] : null;
    }

    /// <summary>Whether a node matches a search: every word typed is in its label, text or path.</summary>
    public static bool Matches(GraphNode node, string query)
    {
        ArgumentNullException.ThrowIfNull(node);
        var words = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(word => node.Label.Contains(word, StringComparison.OrdinalIgnoreCase)
            || node.Text.Contains(word, StringComparison.OrdinalIgnoreCase)
            || node.Path.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A fact's label on the graph: the claim, without the "— yes: …, noticed: …" that hunch answers carry.</summary>
    public static string Short(string fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var claim = fact.Split(" — ", 2)[0].Trim();
        return claim.Length > 48 ? claim[..47].TrimEnd() + "…" : claim;
    }

    private static HashSet<string> Words(string fact)
        => [.. WordPattern().Matches(Short(fact)).Select(match => match.Value.ToLowerInvariant()).Where(word => !Stopwords.Contains(word)).Select(Stem)];

    // "videos"/"video", "coding"/"code" meet in the middle.
    private static string Stem(string word)
        => word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5 ? word[..^3]
            : word.EndsWith('s') && word.Length > 4 ? word[..^1]
            : word.EndsWith('e') && word.Length > 4 ? word[..^1]
            : word;

    [GeneratedRegex(@"[A-Za-z]{4,}")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\[\[([^\[\]|#]+)")]
    private static partial Regex WikiLink();
}

/// <summary>
/// Lays a knowledge graph out by force, as Obsidian does: every node pushes the others away, every
/// edge pulls its ends together, a little gravity keeps it centred. It starts packed in the middle
/// and swarms outward into place. One <see cref="Step"/> per frame; stop once it has settled.
/// </summary>
/// <remarks>ponytail: O(n²) repulsion: fine for a few hundred facts; a Barnes–Hut tree if a profile outgrows that.</remarks>
public sealed class ForceLayout
{
    private const float Repulsion = 5200f;
    private const float Gravity = 0.012f;
    private const float Damping = 0.82f;
    private const float MaxSpeed = 40f;

    private readonly Vector2[] _velocity;
    private readonly (int A, int B, float Length, float Strength)[] _springs;

    /// <summary>How fast a node may move per frame: a burst when it first swarms out, calm when it's only growing.</summary>
    private readonly float _maxSpeed;

    /// <param name="previous">Where nodes were before (by id): they stay put and new ones grow out of their parent, so an edit doesn't reshuffle the map.</param>
    public ForceLayout(KnowledgeMap map, IReadOnlyDictionary<string, Vector2>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        Nodes = map.Nodes;
        Positions = new Vector2[Nodes.Count];
        _velocity = new Vector2[Nodes.Count];
        var index = Nodes.Select((node, i) => (node.Id, i)).ToDictionary(pair => pair.Id, pair => pair.i);
        _springs = [.. map.Edges.Where(edge => index.ContainsKey(edge.From) && index.ContainsKey(edge.To)).Select(edge =>
        {
            var (a, b) = (index[edge.From], index[edge.To]);
            var length = edge.Related ? 130f : Nodes[a].Kind == GraphNodeKind.You || Nodes[b].Kind == GraphNodeKind.You ? 150f : Nodes[a].Kind == GraphNodeKind.Folder ? 100f : 64f;
            return (a, b, length, edge.Related ? 0.008f : 0.05f);
        })];

        // Packed in the middle, spread by a golden-angle spiral (no randomness, so it's the same every time).
        for (var i = 0; i < Positions.Length; i++)
        {
            var angle = i * 2.39996323f;
            Positions[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (4f * MathF.Sqrt(i));
        }

        _maxSpeed = MaxSpeed;
        if (previous is not { Count: > 0 })
        {
            return;
        }

        // Warm start: known nodes where they were; a new one just beside its parent (its topic, folder or you).
        _maxSpeed = CalmSpeed;
        var parent = new Dictionary<int, int>();
        foreach (var edge in map.Edges.Where(edge => !edge.Related && index.ContainsKey(edge.From) && index.ContainsKey(edge.To)))
        {
            parent.TryAdd(index[edge.To], index[edge.From]);
        }

        var placed = new bool[Positions.Length];
        for (var i = 0; i < Positions.Length; i++)
        {
            if (previous.TryGetValue(Nodes[i].Id, out var was))
            {
                Positions[i] = was;
                placed[i] = true;
            }
        }

        for (var pass = 0; pass < 3; pass++) // a new fact in a new topic in a new folder
        {
            for (var i = 0; i < Positions.Length; i++)
            {
                if (!placed[i] && parent.TryGetValue(i, out var p) && placed[p])
                {
                    var angle = i * 2.39996323f;
                    Positions[i] = Positions[p] + (new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 18f);
                    placed[i] = true;
                }
            }
        }
    }

    private const float CalmSpeed = 9f;

    /// <summary>Where every node is, by id (to carry the layout over to the next graph).</summary>
    public IReadOnlyDictionary<string, Vector2> Snapshot()
        => Nodes.Select((node, i) => (node.Id, i)).ToDictionary(pair => pair.Id, pair => Positions[pair.i]);

    public IReadOnlyList<GraphNode> Nodes { get; }

    /// <summary>Where each node is (the user at about 0,0), in the same order as <see cref="Nodes"/>.</summary>
    public Vector2[] Positions { get; }

    /// <summary>A node being dragged: it stays where the pointer puts it, and the rest follow.</summary>
    public int Pinned { get; set; } = -1;

    /// <summary>One frame of the simulation. Returns how much is still moving (near 0 = settled).</summary>
    public float Step()
    {
        var count = Positions.Length;
        var force = new Vector2[count];
        for (var i = 0; i < count; i++)
        {
            for (var j = i + 1; j < count; j++)
            {
                var apart = Positions[i] - Positions[j];
                var distanceSquared = Math.Max(apart.LengthSquared(), 25f);
                var push = apart / MathF.Sqrt(distanceSquared) * (Repulsion / distanceSquared);
                force[i] += push;
                force[j] -= push;
            }
        }

        foreach (var (a, b, length, strength) in _springs)
        {
            var apart = Positions[b] - Positions[a];
            var distance = Math.Max(apart.Length(), 0.01f);
            var pull = apart / distance * ((distance - length) * strength);
            force[a] += pull;
            force[b] -= pull;
        }

        float moving = 0;
        for (var i = 0; i < count; i++)
        {
            if (i == Pinned)
            {
                _velocity[i] = Vector2.Zero;
                continue;
            }

            force[i] -= Positions[i] * Gravity;
            _velocity[i] = (_velocity[i] + force[i]) * Damping;
            if (_velocity[i].Length() > _maxSpeed)
            {
                _velocity[i] = Vector2.Normalize(_velocity[i]) * _maxSpeed;
            }

            Positions[i] += _velocity[i];
            moving += _velocity[i].Length();
        }

        return count == 0 ? 0 : moving / count;
    }

    /// <summary>Wakes it after a change (a node dragged or let go): a little energy so it settles again.</summary>
    public void Nudge()
    {
        for (var i = 0; i < _velocity.Length; i++)
        {
            _velocity[i] += new Vector2(((i * 7) % 5) - 2, ((i * 3) % 5) - 2) * 0.3f;
        }
    }
}
