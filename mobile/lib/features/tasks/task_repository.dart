import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/vault_api.dart';

final taskRepositoryProvider = Provider<TaskRepository>((ref) => TaskRepository(ref.watch(vaultApiProvider)));

final tasksProvider = FutureProvider.autoDispose<TaskList>((ref) => ref.watch(taskRepositoryProvider).list());

/// A `- [ ]` line in a vault note.
class TaskItem {
  const TaskItem(this.path, this.text, this.done);

  final String path;
  final String text;
  final bool done;
}

class TaskList {
  const TaskList(this.open, this.done);

  final List<TaskItem> open;
  final List<TaskItem> done;
}

/// Every `- [ ]` / `- [x]` line in the vault's notes (jv-api /tasks; never Secrets), ticked off in
/// place, and new ones appended to [TaskInbox.path], the same list the PC adds to.
class TaskRepository {
  TaskRepository(this._vault);

  final VaultApi _vault;

  Future<TaskList> list() async {
    final data = (await _vault.call(() => _vault.dio.get('/tasks'))).data as Map;
    List<TaskItem> read(Object? items) => [
          for (final t in (items as List? ?? const []).whereType<Map>())
            if (t['path'] is String && t['text'] is String) TaskItem(t['path'] as String, t['text'] as String, t['done'] == true),
        ];
    return TaskList(read(data['open']), read(data['done']));
  }

  /// Throws a DioException with 409 when the line changed elsewhere (reload and try again).
  Future<void> toggle(TaskItem task, bool done) =>
      _vault.call(() => _vault.dio.post('/tasks/toggle', data: {'path': task.path, 'text': task.text, 'done': done}));

  Future<void> add(String text) => _vault.call(() => _vault.dio.post('/tasks', data: {'text': text}));
}

/// "Add a task: renew passport", read out of a chat or voice request by a plain pattern (no model,
/// so a misread can't invent a task). The same patterns as the PC's Jarvis.Core/Vault/TaskInbox.cs.
class TaskInbox {
  static const path = 'Agent/tasks.md';
  static const _maxLength = 300;
  static const _openers = r'^\s*(?:(?:hey|ok|okay|please|jarvis|can you|could you|would you)[,\s]+)*';

  // "add a task: buy milk", "create a to-do to call mum". Anchored: "how do I add a task in Jira" is a question.
  static final _leading = RegExp(
    '$_openers' r'(?:add|put|create|save|make)\s+(?:a\s+|an\s+)?(?:new\s+)?(?:task|to-?do)(?:\s+item)?\s*(?:to|that|called|saying|for me)?\s*[:,\-]?\s*(?<task>\S.*)$',
    caseSensitive: false,
    dotAll: true,
  );

  // "add buy milk to my to-do list", "put call mum on my tasks".
  static final _trailing = RegExp(
    '$_openers' r'(?:add|put)\s+(?<task>\S.*?)\s+(?:to|on|onto|in)\s+(?:my\s+|the\s+)?(?:task|to-?do)s?(?:\s+list)?\s*[.!?]?$',
    caseSensitive: false,
    dotAll: true,
  );

  /// The task in [request], or null when it isn't asking to add one.
  static String? parse(String request) {
    // "add a task to my calendar" is an event; the PC routes those to the calendar before tasks.
    if (RegExp(r'\bcalendar\b', caseSensitive: false).hasMatch(request)) return null;
    final match = _leading.firstMatch(request) ?? _trailing.firstMatch(request);
    if (match == null) return null;
    final task = match.namedGroup('task')!.replaceAll(RegExp(r'\s+'), ' ').trim().replaceAll(RegExp(r'[.!, ]+$'), '');
    if (task.length < 2) return null;
    return task.length > _maxLength ? task.substring(0, _maxLength) : task;
  }
}
