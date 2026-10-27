import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/sync/data/sync_repository.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_provider.dart';
import 'package:jarvis_mobile/shared/models/sync_result.dart';
import 'package:mockito/mockito.dart';

class _FakeSyncRepo extends Mock implements SyncRepository {
  int calls = 0;
  Completer<SyncResult>? gate;
  Object? failWith;

  @override
  Future<SyncResult> performSync() async {
    calls++;
    if (failWith != null) throw failWith!;
    return gate?.future ?? const SyncResult(pulled: 1);
  }
}

void main() {
  test('overlapping syncs run the repository once', () async {
    final repo = _FakeSyncRepo()..gate = Completer();
    final notifier = SyncNotifier(repo);

    final first = notifier.performSync();
    final second = notifier.performSync(); // e.g. background trigger while button sync runs
    repo.gate!.complete(const SyncResult());
    await Future.wait([first, second]);

    expect(repo.calls, 1);
    expect(notifier.state.status, SyncStatus.complete);
  });

  test('background failure stays quiet and is throttled', () async {
    final repo = _FakeSyncRepo()..failWith = Exception('offline');
    final notifier = SyncNotifier(repo);

    await notifier.performBackgroundSync();
    expect(notifier.state.status, SyncStatus.idle); // no error banner
    await notifier.performBackgroundSync(); // within 2 min → skipped
    expect(repo.calls, 1);
  });

  test('manual sync failure still surfaces', () async {
    final repo = _FakeSyncRepo()..failWith = Exception('boom');
    final notifier = SyncNotifier(repo);
    await notifier.performSync();
    expect(notifier.state.status, SyncStatus.error);
  });
}
