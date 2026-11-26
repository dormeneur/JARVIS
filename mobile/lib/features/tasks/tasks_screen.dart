import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/tasks/task_repository.dart';

/// Every open `- [ ]` in the vault, grouped by note, ticked off in place (the note changes and
/// syncs like any edit). New tasks go to Agent/tasks.md, the list the PC adds to as well.
class TasksScreen extends ConsumerStatefulWidget {
  const TasksScreen({super.key});

  @override
  ConsumerState<TasksScreen> createState() => _TasksScreenState();
}

class _TasksScreenState extends ConsumerState<TasksScreen> {
  final _input = TextEditingController();
  final _busy = <TaskItem>{};
  bool _adding = false;

  @override
  void dispose() {
    _input.dispose();
    super.dispose();
  }

  Future<void> _add() async {
    final text = _input.text.trim();
    if (text.isEmpty || _adding) return;
    setState(() => _adding = true);
    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(taskRepositoryProvider).add(text);
      _input.clear();
      ref.invalidate(tasksProvider);
    } catch (_) {
      messenger.showSnackBar(const SnackBar(content: Text("Couldn't reach your PC. Try again.")));
    } finally {
      if (mounted) setState(() => _adding = false);
    }
  }

  Future<void> _toggle(TaskItem task, bool done) async {
    if (_busy.contains(task)) return;
    setState(() => _busy.add(task));
    final messenger = ScaffoldMessenger.of(context);
    try {
      await ref.read(taskRepositoryProvider).toggle(task, done);
    } on DioException catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text(e.response?.statusCode == 409 ? 'That task changed on another device. Here is the latest.' : "Couldn't reach your PC. Try again."),
      ));
    } finally {
      if (mounted) setState(() => _busy.remove(task));
      ref.invalidate(tasksProvider);
    }
  }

  @override
  Widget build(BuildContext context) {
    final tasks = ref.watch(tasksProvider);
    return Scaffold(
      appBar: AppBar(title: const Text('Tasks')),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
            child: TextField(
              controller: _input,
              textInputAction: TextInputAction.done,
              onSubmitted: (_) => _add(),
              decoration: InputDecoration(
                hintText: 'Add a task',
                border: const OutlineInputBorder(),
                suffixIcon: _adding
                    ? const Padding(padding: EdgeInsets.all(12), child: SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)))
                    : IconButton(tooltip: 'Add', icon: const Icon(Icons.add), onPressed: _add),
              ),
            ),
          ),
          Expanded(
            child: RefreshIndicator(
              onRefresh: () async {
                ref.invalidate(tasksProvider);
                await ref.read(tasksProvider.future).catchError((_) => const TaskList([], []));
              },
              child: tasks.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (_, _) => ListView(children: const [
                  Padding(padding: EdgeInsets.all(32), child: Text('Connect to your PC to see your tasks.', textAlign: TextAlign.center)),
                ]),
                data: (list) => _list(context, list),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _list(BuildContext context, TaskList list) {
    final byNote = <String, List<TaskItem>>{};
    for (final t in list.open) {
      byNote.putIfAbsent(t.path, () => []).add(t);
    }
    final muted = Theme.of(context).textTheme.bodySmall;
    return ListView(
      padding: const EdgeInsets.only(bottom: 24),
      children: [
        if (list.open.isEmpty)
          const Padding(padding: EdgeInsets.all(32), child: Text('Nothing open. Nice.', textAlign: TextAlign.center)),
        for (final entry in byNote.entries) ...[
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
            child: Text(_noteName(entry.key), style: Theme.of(context).textTheme.titleSmall),
          ),
          Padding(padding: const EdgeInsets.fromLTRB(16, 0, 16, 4), child: Text(entry.key, style: muted)),
          for (final t in entry.value) _row(t),
        ],
        if (list.done.isNotEmpty)
          ExpansionTile(
            title: Text('Done (${list.done.length})'),
            children: [for (final t in list.done) _row(t, subtitle: _noteName(t.path))],
          ),
      ],
    );
  }

  Widget _row(TaskItem t, {String? subtitle}) => CheckboxListTile(
        value: t.done,
        controlAffinity: ListTileControlAffinity.leading,
        onChanged: _busy.contains(t) ? null : (v) => _toggle(t, v ?? false),
        title: Text(t.text, style: t.done ? const TextStyle(decoration: TextDecoration.lineThrough) : null),
        subtitle: subtitle == null ? null : Text(subtitle),
      );

  static String _noteName(String path) {
    final name = path.split('/').last;
    return name.toLowerCase().endsWith('.md') ? name.substring(0, name.length - 3) : name;
  }
}
