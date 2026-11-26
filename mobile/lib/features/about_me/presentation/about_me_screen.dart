import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/about_me/data/about_me_repository.dart';
import 'package:jarvis_mobile/features/about_me/domain/knowledge.dart';
import 'package:jarvis_mobile/features/about_me/presentation/knowledge_graph_view.dart';
import 'package:jarvis_mobile/features/home/presentation/home_screen.dart';

/// "About me" on the phone, like the PC's: what Jarvis has learned (Profile/), as a graph or a list,
/// one search for both, "Tell Jarvis something" filed where it belongs, new topics and folders, and
/// a sheet for any dot: edit or delete a fact; rename, edit, add to or delete a topic; delete a folder.
class AboutMeScreen extends ConsumerStatefulWidget {
  const AboutMeScreen({super.key});

  @override
  ConsumerState<AboutMeScreen> createState() => _AboutMeScreenState();
}

class _AboutMeScreenState extends ConsumerState<AboutMeScreen> {
  List<ProfileTopic> _topics = const [];
  KnowledgeMap _map = const KnowledgeMap([], []);
  bool _loading = true;
  bool _graph = true;
  String _status = '';
  String _query = '';
  final _anything = TextEditingController();
  final _search = TextEditingController();

  AboutMeRepository get _repo => ref.read(aboutMeRepositoryProvider);

  @override
  void initState() {
    super.initState();
    _reload();
  }

  @override
  void dispose() {
    _anything.dispose();
    _search.dispose();
    super.dispose();
  }

  Future<void> _reload({String? status}) async {
    setState(() => _loading = true);
    try {
      final topics = await _repo.load();
      final count = topics.fold<int>(0, (n, t) => n + t.facts.length);
      setState(() {
        _topics = topics;
        _map = KnowledgeGraph.build([for (final t in topics) (t.path, t.facts.map((f) => '- $f').join('\n'))]);
        _status = status ?? (count == 0 ? 'Nothing yet. Answer a few hunches, or tell Jarvis something below.' : '$count things Jarvis knows about you.');
      });
    } on AboutMeException catch (e) {
      setState(() => _status = e.message);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _run(Future<String?> Function() action) async {
    try {
      final said = await action();
      await _reload(status: said);
    } on AboutMeException catch (e) {
      setState(() => _status = e.message);
    } catch (_) {
      setState(() => _status = 'Your PC can\'t be reached, so nothing was changed.');
    }
  }

  Future<void> _addAnywhere() async {
    final fact = _anything.text.trim();
    if (fact.isEmpty) return;
    setState(() => _status = 'Finding where that belongs…');
    await _run(() async {
      final path = await _repo.addAnywhere(fact);
      _anything.clear();
      return 'Filed under ${ProfileNotes.title(path)}.';
    });
  }

  Future<void> _newTopic() async {
    final name = await _ask(context, 'New topic', 'e.g. Travel, or people/friends for a folder');
    if (name == null) return;
    await _run(() async => 'Made ${await _repo.addTopic(name)}.');
  }

  @override
  Widget build(BuildContext context) {
    final visible = _query.trim().isEmpty
        ? _topics
        : [
            for (final t in _topics)
              if (_has(t.title) || _has(t.path)) t else if (t.facts.any(_has)) ProfileTopic(t.path, t.facts.where(_has).toList()),
          ];
    return Scaffold(
      backgroundColor: JarvisLook.background,
      appBar: AppBar(
        backgroundColor: JarvisLook.background,
        title: const Text('About me'),
        actions: [
          IconButton(icon: const Icon(Icons.create_new_folder_outlined), tooltip: 'New topic', onPressed: _newTopic),
          IconButton(icon: const Icon(Icons.refresh), tooltip: 'Reload', onPressed: _loading ? null : _reload),
        ],
      ),
      body: Padding(
        padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Text(_status, style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 13)),
          const SizedBox(height: 10),
          TextField(
            controller: _anything,
            textInputAction: TextInputAction.send,
            onSubmitted: (_) => _addAnywhere(),
            decoration: InputDecoration(
              hintText: 'Tell Jarvis something about you…',
              border: const OutlineInputBorder(),
              suffixIcon: IconButton(icon: const Icon(Icons.send), tooltip: 'Add', onPressed: _addAnywhere),
            ),
          ),
          const SizedBox(height: 10),
          Row(children: [
            SegmentedButton<bool>(
              segments: const [ButtonSegment(value: true, label: Text('Graph')), ButtonSegment(value: false, label: Text('List'))],
              selected: {_graph},
              onSelectionChanged: (s) => setState(() => _graph = s.first),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: TextField(
                controller: _search,
                onChanged: (q) => setState(() => _query = q),
                decoration: const InputDecoration(hintText: 'Search', prefixIcon: Icon(Icons.search, size: 18), isDense: true, border: OutlineInputBorder()),
              ),
            ),
          ]),
          const SizedBox(height: 10),
          Expanded(
            child: _loading && _topics.isEmpty
                ? const Center(child: CircularProgressIndicator())
                : _graph
                    ? KnowledgeGraphView(map: _map, query: _query, onPick: (node) => node == null ? null : _open(node))
                    : ListView(children: [
                        for (final topic in visible) ...[
                          ListTile(
                            contentPadding: EdgeInsets.zero,
                            title: Text(topic.title, style: const TextStyle(fontWeight: FontWeight.w600)),
                            subtitle: Text(topic.path, style: const TextStyle(fontSize: 11)),
                            trailing: const Icon(Icons.chevron_right),
                            onTap: () => _open(_map.nodes.firstWhere((n) => n.id == topic.path, orElse: () => GraphNode(topic.path, NodeKind.topic, topic.title, topic.title, topic.path, 0))),
                          ),
                          for (final fact in topic.facts)
                            Card(
                              color: const Color(0x14FFFFFF),
                              child: ListTile(
                                dense: true,
                                title: Text(fact),
                                onTap: () => _open(GraphNode('${topic.path}#', NodeKind.fact, fact, fact, topic.path, 0)),
                              ),
                            ),
                        ],
                      ]),
          ),
        ]),
      ),
    );
  }

  bool _has(String text) => _query.toLowerCase().split(RegExp(r'\s+')).where((w) => w.isNotEmpty).every(text.toLowerCase().contains);

  /// The sheet for a dot: what it is, and what can be done with it.
  void _open(GraphNode node) {
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      backgroundColor: const Color(0xFF0E1322),
      builder: (sheet) => Padding(
        padding: EdgeInsets.fromLTRB(20, 16, 20, 20 + MediaQuery.viewInsetsOf(sheet).bottom),
        child: SingleChildScrollView(child: _detail(sheet, node)),
      ),
    );
  }

  Widget _detail(BuildContext sheet, GraphNode node) {
    void done() => Navigator.of(sheet).pop();
    Widget heading(String text) => Padding(
          padding: const EdgeInsets.only(bottom: 10),
          child: Text(text, style: const TextStyle(color: JarvisLook.textPrimary, fontSize: 17, fontWeight: FontWeight.w600)),
        );
    switch (node.kind) {
      case NodeKind.fact:
        final text = TextEditingController(text: node.text);
        return Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
          heading(ProfileNotes.title(node.path)),
          TextField(controller: text, maxLines: null, decoration: const InputDecoration(border: OutlineInputBorder())),
          const SizedBox(height: 12),
          Row(children: [
            FilledButton(onPressed: () { done(); _run(() async => await _repo.saveFact(node.path, node.text, text.text) ? 'Saved.' : 'That line changed elsewhere, so here\'s the latest.'); }, child: const Text('Save')),
            const SizedBox(width: 8),
            TextButton(onPressed: () async { if (await _confirm(sheet, 'Delete this fact?')) { done(); _run(() async => await _repo.deleteFact(node.path, node.text) ? 'Deleted.' : 'That line changed elsewhere, so here\'s the latest.'); } }, child: const Text('Delete')),
          ]),
          Text(node.path, style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 11)),
        ]);
      case NodeKind.topic:
        final topic = _topics.firstWhere((t) => t.path == node.path, orElse: () => ProfileTopic(node.path, const []));
        final add = TextEditingController();
        return Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
          heading(node.label),
          for (final fact in topic.facts)
            ListTile(
              contentPadding: EdgeInsets.zero,
              dense: true,
              title: Text(fact),
              trailing: const Icon(Icons.edit, size: 16),
              onTap: () { done(); _open(GraphNode('${topic.path}#', NodeKind.fact, fact, fact, topic.path, 0)); },
            ),
          TextField(
            controller: add,
            textInputAction: TextInputAction.send,
            onSubmitted: (_) { done(); _run(() async { await _repo.addFact(node.path, add.text); return 'Added.'; }); },
            decoration: const InputDecoration(hintText: 'Add to this topic…', border: OutlineInputBorder()),
          ),
          const SizedBox(height: 12),
          Wrap(spacing: 8, children: [
            OutlinedButton(onPressed: () async {
              final name = await _ask(sheet, 'Rename topic', 'New name (people/friends moves it)');
              if (name == null) return;
              done();
              _run(() async => 'Renamed to ${ProfileNotes.title(await _repo.renameTopic(node.path, name))}.');
            }, child: const Text('Rename')),
            TextButton(onPressed: () async {
              if (await _confirm(sheet, 'Delete ${node.label} and its ${topic.facts.length} facts?')) {
                done();
                _run(() async { await _repo.delete(node.path); return 'Deleted ${node.path}.'; });
              }
            }, child: const Text('Delete topic')),
          ]),
          Text(node.path, style: const TextStyle(color: JarvisLook.textSecondary, fontSize: 11)),
        ]);
      case NodeKind.folder:
        final inside = _topics.where((t) => ProfileNotes.folderOf(t.path) == node.path).toList();
        return Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
          heading(node.label),
          Text(inside.isEmpty ? 'No topics in it yet.' : inside.map((t) => t.title.split(' / ').last).join(' · '), style: const TextStyle(color: JarvisLook.textSecondary)),
          const SizedBox(height: 12),
          TextButton(onPressed: () async {
            if (await _confirm(sheet, 'Delete ${node.label} and all ${inside.length} topics in it?')) {
              done();
              _run(() async { await _repo.delete(node.path, folder: true); return 'Deleted the folder ${node.path}.'; });
            }
          }, child: const Text('Delete folder')),
        ]);
      case NodeKind.you:
        return Column(crossAxisAlignment: CrossAxisAlignment.start, mainAxisSize: MainAxisSize.min, children: [
          heading('You'),
          const Text('Everything around you is what Jarvis has learned. Tell it more above, answer its hunches, or tap any dot to edit it.', style: TextStyle(color: JarvisLook.textSecondary, height: 1.4)),
        ]);
    }
  }

  static Future<bool> _confirm(BuildContext context, String question) async =>
      await showDialog<bool>(
        context: context,
        builder: (d) => AlertDialog(
          content: Text(question),
          actions: [
            TextButton(onPressed: () => Navigator.of(d).pop(false), child: const Text('Cancel')),
            FilledButton(onPressed: () => Navigator.of(d).pop(true), child: const Text('Delete')),
          ],
        ),
      ) ??
      false;

  static Future<String?> _ask(BuildContext context, String title, String hint) {
    final text = TextEditingController();
    return showDialog<String>(
      context: context,
      builder: (d) => AlertDialog(
        title: Text(title),
        content: TextField(controller: text, autofocus: true, decoration: InputDecoration(hintText: hint), onSubmitted: (v) => Navigator.of(d).pop(v.trim().isEmpty ? null : v.trim())),
        actions: [
          TextButton(onPressed: () => Navigator.of(d).pop(), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.of(d).pop(text.text.trim().isEmpty ? null : text.text.trim()), child: const Text('OK')),
        ],
      ),
    );
  }
}
