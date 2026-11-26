import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/relay/relay_repository.dart';
import 'package:jarvis_mobile/features/tasks/task_repository.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  // The same cases as the PC's TaskInboxTests.cs.
  test('reads the task out of an add request', () {
    const cases = {
      'add a task: buy milk': 'buy milk',
      'Add a task to call mum.': 'call mum',
      'add task renew my passport': 'renew my passport',
      'create a to-do: finish the report': 'finish the report',
      'add buy milk to my to-do list': 'buy milk',
      'put call the bank on my tasks': 'call the bank',
      'jarvis, add a task   pay   rent!': 'pay rent',
    };
    cases.forEach((request, task) => expect(TaskInbox.parse(request), task, reason: request));
  });

  test('leaves questions and other requests alone', () {
    for (final request in ['what tasks do I have', 'add a task', 'add a task:', 'how do I add a task in Jira', "what's on my calendar today", 'add a task to my calendar']) {
      expect(TaskInbox.parse(request), isNull, reason: request);
    }
  });

  test('a job this phone sent is announced once, when it ends', () async {
    SharedPreferences.setMockInitialValues({});
    var status = 'running';
    final shown = <String>[];
    final watcher = RelayWatcher(
      () async => [RelayJob.fromJson({'id': 'r1', 'title': 'Tidy', 'brief': 'Tidy my desktop', 'status': status, 'result': 'Moved 12 files.'})],
      (job) async => shown.add('${job.id}:${job.status}'),
    );
    await watcher.track('r1');
    await watcher.check();
    expect(shown, isEmpty);

    status = 'finished';
    await watcher.check();
    await watcher.check();
    expect(shown, ['r1:finished']);
    watcher.dispose();
  });
}
