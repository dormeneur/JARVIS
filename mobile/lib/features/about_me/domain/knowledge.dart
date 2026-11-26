import 'dart:math' as math;
import 'dart:ui' show Offset;

/// What Jarvis has learned about the user, on the phone: the same rules as the PC's ProfileNotes,
/// KnowledgeGraph and ForceLayout (desktop/src/Jarvis.Core/Vault), so both apps read and edit the
/// Profile files identically. Every Markdown file in Profile/ (one folder deep) is a topic; each
/// "- " line one fact; everything else in a file is kept as it is.
class ProfileNotes {
  ProfileNotes._();

  static const folder = 'Profile';
  static const traits = 'Profile/traits.md';
  static const notMe = 'Profile/not-me.md';
  static const otherFolders = {'forms', 'resume', 'documents'};
  static const _hidden = {'hunches.md'};
  static const _maxFact = 500;

  /// The Profile files to show, learned traits first, wrong guesses last.
  static List<String> files(Iterable<({String path, String name, bool isFolder})> entries) {
    final found = entries.where((e) => !e.isFolder && e.name.toLowerCase().endsWith('.md') && !_hidden.contains(e.name.toLowerCase())).map((e) => e.path).toList();
    int rank(String path) => path == traits ? 0 : path == notMe ? 2 : 1;
    found.sort((a, b) {
      final byRank = rank(a).compareTo(rank(b));
      return byRank != 0 ? byRank : a.toLowerCase().compareTo(b.toLowerCase());
    });
    return found;
  }

  static String title(String path) {
    if (path == traits) return 'What Jarvis knows about you';
    if (path == notMe) return 'Wrong guesses (never asked again)';
    String words(String part) {
      final w = part.replaceAll('-', ' ').replaceAll('_', ' ');
      return w.isEmpty ? part : w[0].toUpperCase() + w.substring(1);
    }

    final inFolder = path.startsWith('$folder/') ? path.substring(folder.length + 1) : path;
    final slash = inFolder.indexOf('/');
    final prefix = slash > 0 ? '${words(inFolder.substring(0, slash))} / ' : '';
    final name = inFolder.split('/').last.replaceAll(RegExp(r'\.md$'), '');
    return name.isEmpty ? path : prefix + words(name);
  }

  static List<String> facts(String? text) => _lines(text)
      .where(_isFact)
      .map((line) => line.trimLeft().substring(2).trim())
      .where((fact) => fact.isNotEmpty)
      .toList();

  /// A file of facts, the traits file, or a new topic (nothing but headings): pages of paragraphs aren't shown.
  static bool isKnowledge(String path, String? text) =>
      path == traits || facts(text).isNotEmpty || _lines(text).every((line) => line.trim().isEmpty || line.trimLeft().startsWith('#'));

  /// The file with one fact changed (or removed, when [replacement] is null or blank); null when that
  /// fact isn't there any more (changed elsewhere: reload).
  static String? replace(String? text, String fact, String? replacement) {
    final lines = _lines(text).toList();
    final at = lines.indexWhere((line) => _isFact(line) && line.trimLeft().substring(2).trim() == fact.trim());
    if (at < 0) return null;
    final clean = _clean(replacement);
    if (clean.isEmpty) {
      lines.removeAt(at);
    } else {
      final indent = lines[at].substring(0, lines[at].length - lines[at].trimLeft().length);
      lines[at] = '$indent- $clean';
    }
    return lines.join('\n');
  }

  static String add(String? text, String fact, String path) {
    final clean = _clean(fact);
    if (clean.isEmpty) return text ?? '';
    if (text == null || text.trim().isEmpty) return '# ${title(path)}\n\n- $clean\n';
    return '${text.endsWith('\n') ? text : '$text\n'}- $clean\n';
  }

  /// "Travel" → Profile/travel.md, "people/Family" → Profile/people/family.md; null when unusable.
  static String? topicPath(String name) {
    final parts = name.replaceAll('\\', '/').split('/').map(_slug).where((p) => p.isNotEmpty).toList();
    if (parts.isEmpty || parts.length > 2 || (parts.length == 2 && otherFolders.contains(parts[0]))) return null;
    return '$folder/${parts.length == 2 ? '${parts[0]}/' : ''}${parts.last}.md';
  }

  static String? folderOf(String path) {
    final cut = path.lastIndexOf('/');
    return cut > folder.length ? path.substring(0, cut) : null;
  }

  static String _slug(String part) {
    var slug = part.toLowerCase().replaceAll(RegExp(r'[^a-z0-9]'), '-').replaceAll(RegExp(r'-+'), '-');
    slug = slug.replaceAll(RegExp(r'^-+|-+$'), '');
    return slug.length > 40 ? slug.substring(0, 40).replaceAll(RegExp(r'-+$'), '') : slug;
  }

  static String _clean(String? fact) {
    final line = (fact ?? '').split(RegExp(r'\s+')).where((w) => w.isNotEmpty).join(' ');
    return line.length > _maxFact ? line.substring(0, _maxFact) : line;
  }

  static bool _isFact(String line) => line.trimLeft().startsWith('- ');

  static Iterable<String> _lines(String? text) => (text ?? '').replaceAll('\r\n', '\n').split('\n');
}

enum NodeKind { you, folder, topic, fact }

class GraphNode {
  const GraphNode(this.id, this.kind, this.label, this.text, this.path, this.group);

  final String id;
  final NodeKind kind;
  final String label;

  /// The fact as in the file (for editing), or the name.
  final String text;
  final String path;

  /// Its topic's index, for its colour (−1 you, −2 a folder).
  final int group;

  bool matches(String query) {
    final words = query.toLowerCase().split(RegExp(r'\s+')).where((w) => w.isNotEmpty);
    final haystack = '$label $text $path'.toLowerCase();
    return words.isNotEmpty && words.every(haystack.contains);
  }
}

class GraphEdge {
  const GraphEdge(this.from, this.to, {this.related = false});

  final String from;
  final String to;

  /// Two facts about the same thing (drawn fainter than structure).
  final bool related;
}

class KnowledgeMap {
  const KnowledgeMap(this.nodes, this.edges);

  final List<GraphNode> nodes;
  final List<GraphEdge> edges;
}

/// You in the middle, folders and topics around you, facts around their topic, related facts joined.
class KnowledgeGraph {
  KnowledgeGraph._();

  static const youId = 'you';
  static const _relatedPerFact = 3;
  static const _stop = {
    'that', 'this', 'with', 'from', 'have', 'your', 'they', 'them', 'their', 'what', 'when', 'where', 'which', 'would',
    'could', 'should', 'about', 'there', 'been', 'were', 'just', 'like', 'more', 'most', 'some', 'only', 'also', 'very',
    'into', 'over', 'than', 'then', 'said', 'says', 'noticed', 'partly', 'words', 'spot', 'really', 'always', 'often',
    'usually', 'every', 'much', 'many', 'make', 'does',
  };

  static KnowledgeMap build(List<(String, String?)> files) {
    final nodes = <GraphNode>[const GraphNode(youId, NodeKind.you, 'You', 'You', '', -1)];
    final edges = <GraphEdge>[];
    final facts = <(GraphNode, Set<String>)>[];
    final folders = <String, String>{};
    for (var group = 0; group < files.length; group++) {
      final (path, text) = files[group];
      final topic = GraphNode(path, NodeKind.topic, ProfileNotes.title(path), ProfileNotes.title(path), path, group);
      nodes.add(topic);
      var parent = youId;
      final folder = ProfileNotes.folderOf(path);
      if (folder != null) {
        parent = folders.putIfAbsent(folder, () {
          final name = folder.split('/').last;
          final id = '$folder/';
          nodes.add(GraphNode(id, NodeKind.folder, name[0].toUpperCase() + name.substring(1).replaceAll('-', ' '), name, folder, -2));
          edges.add(GraphEdge(youId, id));
          return id;
        });
      }
      edges.add(GraphEdge(parent, topic.id));
      var index = 0;
      for (final fact in ProfileNotes.facts(text)) {
        final node = GraphNode('$path#${index++}', NodeKind.fact, short(fact), fact, path, group);
        nodes.add(node);
        edges.add(GraphEdge(topic.id, node.id));
        facts.add((node, _words(fact)));
      }
    }

    final seen = <String>{};
    for (var i = 0; i < facts.length; i++) {
      final best = <(int, int)>[];
      for (var j = 0; j < facts.length; j++) {
        if (j == i) continue;
        final shared = facts[i].$2.intersection(facts[j].$2).length;
        if (shared >= 2 || (shared == 1 && facts[i].$1.path != facts[j].$1.path)) best.add((j, shared));
      }
      best.sort((a, b) => b.$2.compareTo(a.$2));
      for (final (j, _) in best.take(_relatedPerFact)) {
        final a = facts[i].$1.id, b = facts[j].$1.id;
        final key = a.compareTo(b) < 0 ? '$a|$b' : '$b|$a';
        if (seen.add(key)) edges.add(GraphEdge(a, b, related: true));
      }
    }
    return KnowledgeMap(nodes, edges);
  }

  /// A fact's label: the claim, without the "— yes: …, noticed: …" hunch answers carry.
  static String short(String fact) {
    final claim = fact.split(' — ').first.trim();
    return claim.length > 48 ? '${claim.substring(0, 47).trimRight()}…' : claim;
  }

  static Set<String> _words(String fact) => RegExp(r'[A-Za-z]{4,}')
      .allMatches(short(fact))
      .map((m) => m.group(0)!.toLowerCase())
      .where((w) => !_stop.contains(w))
      .map(_stem)
      .toSet();

  static String _stem(String w) => w.endsWith('ing') && w.length > 5
      ? w.substring(0, w.length - 3)
      : w.endsWith('s') && w.length > 4
          ? w.substring(0, w.length - 1)
          : w.endsWith('e') && w.length > 4
              ? w.substring(0, w.length - 1)
              : w;
}

/// Force layout, as on the PC: everything repels, edges pull, a little gravity centres it. A warm start
/// keeps known nodes where they were and grows new ones beside their parent, so edits don't reshuffle.
class ForceLayout {
  ForceLayout(KnowledgeMap map, {Map<String, Offset>? previous})
      : nodes = map.nodes,
        positions = List.filled(map.nodes.length, Offset.zero),
        _velocity = List.filled(map.nodes.length, Offset.zero),
        _maxSpeed = (previous == null || previous.isEmpty) ? 40 : 9 {
    final index = {for (var i = 0; i < nodes.length; i++) nodes[i].id: i};
    for (final edge in map.edges) {
      final a = index[edge.from], b = index[edge.to];
      if (a == null || b == null) continue;
      final length = edge.related ? 130.0 : (nodes[a].kind == NodeKind.you || nodes[b].kind == NodeKind.you) ? 150.0 : nodes[a].kind == NodeKind.folder ? 100.0 : 64.0;
      _springs.add((a, b, length, edge.related ? 0.008 : 0.05));
    }
    for (var i = 0; i < positions.length; i++) {
      final angle = i * 2.39996323;
      positions[i] = Offset(math.cos(angle), math.sin(angle)) * (4 * math.sqrt(i));
    }
    if (previous == null || previous.isEmpty) return;
    final parent = <int, int>{};
    for (final edge in map.edges.where((e) => !e.related)) {
      final a = index[edge.from], b = index[edge.to];
      if (a != null && b != null) parent.putIfAbsent(b, () => a);
    }
    final placed = List.filled(positions.length, false);
    for (var i = 0; i < positions.length; i++) {
      final was = previous[nodes[i].id];
      if (was != null) {
        positions[i] = was;
        placed[i] = true;
      }
    }
    for (var pass = 0; pass < 3; pass++) {
      for (var i = 0; i < positions.length; i++) {
        final p = parent[i];
        if (!placed[i] && p != null && placed[p]) {
          final angle = i * 2.39996323;
          positions[i] = positions[p] + Offset(math.cos(angle), math.sin(angle)) * 18;
          placed[i] = true;
        }
      }
    }
  }

  static const _repulsion = 5200.0;
  static const _gravity = 0.012;
  static const _damping = 0.82;

  final List<GraphNode> nodes;
  final List<Offset> positions;
  final List<Offset> _velocity;
  final List<(int, int, double, double)> _springs = [];
  final double _maxSpeed;

  /// A node being dragged: it stays where the finger puts it.
  int pinned = -1;

  Map<String, Offset> snapshot() => {for (var i = 0; i < nodes.length; i++) nodes[i].id: positions[i]};

  /// One frame. Returns how much is still moving (near 0 = settled).
  double step() {
    final n = positions.length;
    final force = List.filled(n, Offset.zero);
    for (var i = 0; i < n; i++) {
      for (var j = i + 1; j < n; j++) {
        final apart = positions[i] - positions[j];
        final d2 = math.max(apart.distanceSquared, 25.0);
        final push = apart / math.sqrt(d2) * (_repulsion / d2);
        force[i] += push;
        force[j] -= push;
      }
    }
    for (final (a, b, length, strength) in _springs) {
      final apart = positions[b] - positions[a];
      final d = math.max(apart.distance, 0.01);
      final pull = apart / d * ((d - length) * strength);
      force[a] += pull;
      force[b] -= pull;
    }
    var moving = 0.0;
    for (var i = 0; i < n; i++) {
      if (i == pinned) {
        _velocity[i] = Offset.zero;
        continue;
      }
      var v = (_velocity[i] + force[i] - positions[i] * _gravity) * _damping;
      if (v.distance > _maxSpeed) v = v / v.distance * _maxSpeed;
      _velocity[i] = v;
      positions[i] += v;
      moving += v.distance;
    }
    return n == 0 ? 0 : moving / n;
  }
}
