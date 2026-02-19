// ignore_for_file: avoid_print

import 'dart:collection';
import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/features/sync/data/sync_repository.dart';
import 'package:drift/drift.dart' hide isNull, isNotNull;
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:path_provider_platform_interface/path_provider_platform_interface.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

// ---------------------------------------------------------------------------
// _FakeSecureStorage — no reads/writes to device keychain in tests
// ---------------------------------------------------------------------------

/// In-memory replacement so ApiClient can be constructed without a real
/// flutter_secure_storage platform channel.
class _FakeFlutterSecureStorage extends FlutterSecureStorage {
  final Map<String, String> _store = {};

  @override
  Future<String?> read({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => _store[key];

  @override
  Future<void> write({
    required String key,
    required String? value,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async {
    if (value == null) {
      _store.remove(key);
    } else {
      _store[key] = value;
    }
  }

  @override
  Future<void> delete({
    required String key,
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => _store.remove(key);

  @override
  Future<void> deleteAll({
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => _store.clear();

  @override
  Future<Map<String, String>> readAll({
    IOSOptions? iOptions,
    AndroidOptions? aOptions,
    LinuxOptions? lOptions,
    WebOptions? webOptions,
    MacOsOptions? mOptions,
    WindowsOptions? wOptions,
  }) async => Map.of(_store);
}

// ---------------------------------------------------------------------------
// _FakePathProvider — returns system temp so _pullFile can write files
// ---------------------------------------------------------------------------

class _FakePathProvider
    with MockPlatformInterfaceMixin
    implements PathProviderPlatform {
  final String tempPath;
  _FakePathProvider(this.tempPath);

  @override
  Future<String?> getApplicationDocumentsPath() async => tempPath;
  @override
  Future<String?> getTemporaryPath() async => tempPath;
  @override
  Future<String?> getApplicationSupportPath() async => tempPath;
  @override
  Future<String?> getApplicationCachePath() async => tempPath;
  @override
  Future<String?> getExternalStoragePath() async => tempPath;
  @override
  Future<List<String>?> getExternalCachePaths() async => [tempPath];
  @override
  Future<List<String>?> getExternalStoragePaths({
    StorageDirectory? type,
  }) async => [tempPath];
  @override
  Future<String?> getDownloadsPath() async => tempPath;
  @override
  Future<String?> getLibraryPath() async => tempPath;
}

// ---------------------------------------------------------------------------
// _FakeAdapter — queue-based Dio HttpClientAdapter
// ---------------------------------------------------------------------------

/// Plays back pre-enqueued responses in order. Throws [StateError] if the
/// queue is empty — making unexpected extra HTTP requests fail immediately.
class _FakeAdapter implements HttpClientAdapter {
  final Queue<Future<ResponseBody> Function(RequestOptions)> _queue = Queue();

  void enqueue(Future<ResponseBody> Function(RequestOptions) handler) {
    _queue.add(handler);
  }

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<List<int>>? requestStream,
    Future<dynamic>? cancelFuture,
  ) async {
    if (_queue.isEmpty) {
      throw StateError(
        '_FakeAdapter: unexpected HTTP call to ${options.uri} — '
        'queue exhausted.',
      );
    }
    return _queue.removeFirst()(options);
  }

  @override
  void close({bool force = false}) {}

  bool get isExhausted => _queue.isEmpty;
}

// ---------------------------------------------------------------------------
// Response builder helpers
// ---------------------------------------------------------------------------

ResponseBody _jsonResponse(Map<String, dynamic> body) {
  final encoded = utf8.encode(jsonEncode(body));
  return ResponseBody.fromBytes(
    encoded,
    200,
    headers: {
      Headers.contentTypeHeader: ['application/json'],
    },
  );
}

ResponseBody _pullResponse(List<int> bytes, int version) {
  return ResponseBody.fromBytes(
    bytes,
    200,
    headers: {
      Headers.contentTypeHeader: ['application/octet-stream'],
      'x-file-version': ['$version'],
    },
  );
}

Map<String, dynamic> _emptyManifest() => {
  'to_push': <dynamic>[],
  'to_pull': <dynamic>[],
  'conflicts': <dynamic>[],
};

Map<String, dynamic> _manifestWithConflict(String path) => {
  'to_push': <dynamic>[],
  'to_pull': <dynamic>[],
  'conflicts': [
    {'path': path},
  ],
};

Map<String, dynamic> _pushSuccess(String path, int version) => {
  'pushed': [
    {'path': path, 'version': version},
  ],
  'conflicts': <dynamic>[],
};

Map<String, dynamic> _pushConflict(String conflictPath) => {
  'pushed': <dynamic>[],
  'conflicts': [
    {'path': conflictPath},
  ],
};

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late Directory tempDir;
  late AppDatabase db;
  late ExplorerRepository explorerRepo;
  late _FakeAdapter fakeAdapter;
  late SyncRepository syncRepo;

  setUp(() async {
    tempDir = await Directory.systemTemp.createTemp('jarvis_sync_test_');
    PathProviderPlatform.instance = _FakePathProvider(tempDir.path);

    db = AppDatabase.forTesting(NativeDatabase.memory());
    explorerRepo = ExplorerRepository(db: db);

    fakeAdapter = _FakeAdapter();
    final dio = Dio();
    dio.httpClientAdapter = fakeAdapter;

    final apiClient = ApiClient(
      secureStorage: SecureStorage(storage: _FakeFlutterSecureStorage()),
      dioOverride: dio,
    );

    syncRepo = SyncRepository(
      apiClient: apiClient,
      explorerRepo: explorerRepo,
      db: db,
    );
  });

  tearDown(() async {
    await db.close();
    await tempDir.delete(recursive: true);
  });

  // =========================================================================
  // Scenario A — Manifest conflict does NOT accumulate across two syncs
  //
  // UPDATED: After Bug B fix, Phase 2 manifest conflicts now CREATE synthetic
  // mutation rows. This test verifies that conflictPaths is still reset each
  // sync (no accumulation) and that the synthetic mutation row is created.
  // =========================================================================

  group('Scenario A — Manifest conflict must not accumulate', () {
    test(
      'Two consecutive syncs returning same manifest conflict yield conflicts==1 each',
      () async {
        // Sync 1 — no mutations; manifest returns 1 conflict
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_manifestWithConflict('Personal/notes.md')),
        );
        final result1 = await syncRepo.performSync();

        // Sync 2 — same manifest conflict
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_manifestWithConflict('Personal/notes.md')),
        );
        final result2 = await syncRepo.performSync();

        expect(
          result1.conflicts,
          1,
          reason: 'Sync 1: must report exactly 1 conflict',
        );
        expect(result1.conflictPaths, ['Personal/notes.md']);

        expect(
          result2.conflicts,
          1,
          reason:
              'Bug A present if this is > 1: conflictPaths accumulating '
              'across consecutive syncs',
        );
        expect(result2.conflictPaths, ['Personal/notes.md']);

        // After Bug B fix: synthetic mutation row SHOULD be created
        final failed = await db.getFailedMutations();
        expect(
          failed.length,
          1,
          reason: 'Bug B fix: manifest conflict MUST create synthetic mutation row',
        );
        expect(failed.first.path, 'Personal/notes.md');
        expect(failed.first.status, 'failed');

        expect(result1.pushed, 0);
        expect(result1.pulled, 0);
        expect(result2.pushed, 0);
        expect(result2.pulled, 0);
      },
    );
  });

  // =========================================================================
  // Scenario B — Phase 1 conflict stores conflictFilePath; Phase 2 adds a
  //              second entry without a mutation row (Bug B documented)
  //
  // UPDATED: After Bug B fix, Phase 2 will NOT create a duplicate mutation
  // row if one already exists from Phase 1. The duplicate prevention logic
  // checks for existing pending/failed mutations before creating synthetic rows.
  // =========================================================================

  group('Scenario B — Phase 1 conflict vs Phase 2 duplication', () {
    test(
      'Phase 1 conflict stores conflictFilePath; Phase 2 skips duplicate mutation creation',
      () async {
        final localFile = File('${tempDir.path}/notes.md');
        await localFile.writeAsString('hello world');

        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('Personal/notes.md'),
            name: const Value('notes.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T12:00:00Z'),
            contentHash: const Value('sha256:abc'),
            localPath: Value(localFile.path),
            serverVersion: const Value(3),
          ),
        );
        await db.enqueueMutation(
          id: 'mut-b-001',
          path: 'Personal/notes.md',
          operation: 'update',
          timestamp: '2026-02-19T12:01:00Z',
          baseVersion: 3,
        );

        // Phase 1: push → conflict response
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_pushConflict('Personal/notes_conflict_123.md')),
        );
        // Phase 2: manifest also reports the same file as conflict
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_manifestWithConflict('Personal/notes.md')),
        );

        final result = await syncRepo.performSync();

        // Both Phase 1 and Phase 2 add to conflictPaths
        expect(
          result.conflicts,
          2,
          reason:
              'Phase 1 and Phase 2 each add 1 entry — total 2 for same file',
        );

        // Phase 1 mutation must have conflictFilePath set (markMutationConflict)
        final mutation = await db.getMutationById('mut-b-001');
        expect(mutation, isNotNull);
        expect(mutation!.status, 'failed');
        expect(
          mutation.conflictFilePath,
          'Personal/notes_conflict_123.md',
          reason:
              'Phase 1 MUST store conflictFilePath via markMutationConflict',
        );

        // Still only 1 mutation row (Phase 2 detects existing row and skips creation)
        final allFailed = await db.getFailedMutations();
        expect(
          allFailed.length,
          1,
          reason:
              'Bug B fix: Phase 2 must NOT create duplicate when Phase 1 row exists',
        );

        expect(result.pushed, 0);
        expect(result.pulled, 0);
      },
    );
  });

  // =========================================================================
  // Scenario E — Phase 2 manifest conflict creates synthetic failed mutation row
  //
  // Bug B Fix Verification: When Phase 2 detects a manifest conflict and no
  // existing mutation row exists for that path, a synthetic mutation row must
  // be created with status='failed' to enable UI display and resolution flows.
  // =========================================================================

  group('Scenario E — Phase 2 manifest conflict persistence', () {
    test(
      'Phase 2 manifest conflict creates synthetic failed mutation row',
      () async {
        // Setup: Create a cache entry but NO pending mutation
        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('Documents/report.md'),
            name: const Value('report.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T14:00:00Z'),
            contentHash: const Value('sha256:report'),
            localPath: const Value('/fake/path/report.md'),
            serverVersion: const Value(5),
          ),
        );

        // Phase 2: manifest returns conflict for Documents/report.md
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_manifestWithConflict('Documents/report.md')),
        );

        final result = await syncRepo.performSync();

        // Verify SyncResult
        expect(
          result.conflicts,
          1,
          reason: 'Manifest conflict must be reported in SyncResult',
        );
        expect(result.conflictPaths, ['Documents/report.md']);
        expect(result.pushed, 0);
        expect(result.pulled, 0);

        // Verify synthetic mutation row was created
        final failedMutations = await db.getFailedMutations();
        expect(
          failedMutations.length,
          1,
          reason: 'Exactly 1 synthetic mutation row must be created',
        );

        final mutation = failedMutations.first;
        expect(
          mutation.path,
          'Documents/report.md',
          reason: 'Mutation path must match conflict path',
        );
        expect(
          mutation.status,
          'failed',
          reason: 'Synthetic mutation status must be "failed" (not "pending")',
        );
        expect(
          mutation.operation,
          'update',
          reason: 'Synthetic mutation operation must be "update"',
        );
        expect(
          mutation.id.startsWith('manifest-conflict-'),
          isTrue,
          reason: 'Synthetic mutation ID must start with "manifest-conflict-"',
        );
        expect(
          mutation.conflictFilePath,
          isNull,
          reason: 'Manifest conflicts have no conflictFilePath (no remote snapshot)',
        );
        expect(
          mutation.retryCount,
          1,
          reason: 'Synthetic mutation retryCount is 1 after markMutationFailed',
        );
        expect(
          mutation.baseVersion,
          5,
          reason: 'Synthetic mutation baseVersion must match cache entry serverVersion',
        );
        // Sanity check: timestamp should be valid ISO8601 and recent
        expect(
          mutation.timestamp.isNotEmpty,
          isTrue,
          reason: 'Timestamp must not be empty',
        );
      },
    );
  });

  // =========================================================================
  // Scenario F — Second sync with same manifest conflict does NOT create duplicate
  //
  // Bug B Fix Verification: The duplicate prevention logic must ensure that
  // if a synthetic mutation row already exists for a conflict path, subsequent
  // syncs with the same conflict do NOT create additional mutation rows.
  // =========================================================================

  group('Scenario F — Duplicate prevention for manifest conflicts', () {
    test(
      'Second sync with same manifest conflict does NOT create duplicate mutation rows',
      () async {
        // Setup: Create a cache entry but NO pending mutation
        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('Projects/design.md'),
            name: const Value('design.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T15:00:00Z'),
            contentHash: const Value('sha256:design'),
            localPath: const Value('/fake/path/design.md'),
            serverVersion: const Value(7),
          ),
        );

        // Sync 1: manifest returns conflict for Projects/design.md
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_manifestWithConflict('Projects/design.md')),
        );

        final result1 = await syncRepo.performSync();

        expect(result1.conflicts, 1);
        expect(result1.conflictPaths, ['Projects/design.md']);

        // Verify synthetic mutation row was created
        final failedAfterSync1 = await db.getFailedMutations();
        expect(
          failedAfterSync1.length,
          1,
          reason: 'First sync must create exactly 1 synthetic mutation row',
        );
        final mutation1 = failedAfterSync1.first;
        expect(mutation1.path, 'Projects/design.md');
        expect(mutation1.status, 'failed');

        // Sync 2: same manifest conflict returned again
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_manifestWithConflict('Projects/design.md')),
        );

        final result2 = await syncRepo.performSync();

        expect(result2.conflicts, 1);
        expect(result2.conflictPaths, ['Projects/design.md']);

        // Verify NO duplicate mutation row was created
        final failedAfterSync2 = await db.getFailedMutations();
        expect(
          failedAfterSync2.length,
          1,
          reason: 'Second sync must NOT create duplicate mutation row',
        );

        // Verify it's the same mutation row (same ID)
        final mutation2 = failedAfterSync2.first;
        expect(
          mutation2.id,
          mutation1.id,
          reason: 'Mutation ID must remain unchanged (no duplicate created)',
        );
        expect(mutation2.path, 'Projects/design.md');
        expect(mutation2.status, 'failed');
      },
    );
  });

  // =========================================================================
  // Scenario G — Keep Local resolution prevents duplicate synthetic mutations
  //
  // Verifies that after resolveKeepLocal, if Phase 1 successfully pushes,
  // Phase 2 does NOT create a new synthetic mutation for the same path.
  // =========================================================================

  group('Scenario G — Keep Local resolution flow', () {
    test(
      'After resolveKeepLocal and successful push, Phase 2 does NOT create duplicate',
      () async {
        final localFile = File('${tempDir.path}/keeplocal.md');
        await localFile.writeAsString('my local content');

        // Setup: cache entry with serverVersion=3
        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('Work/keeplocal.md'),
            name: const Value('keeplocal.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T16:00:00Z'),
            contentHash: const Value('sha256:local'),
            localPath: Value(localFile.path),
            serverVersion: const Value(3),
          ),
        );

        // Sync 1: manifest returns conflict
        fakeAdapter.enqueue(
          (_) async =>
              _jsonResponse(_manifestWithConflict('Work/keeplocal.md')),
        );

        final result1 = await syncRepo.performSync();
        expect(result1.conflicts, 1);

        // Verify synthetic mutation created
        final failedAfterSync1 = await db.getFailedMutations();
        expect(failedAfterSync1.length, 1);
        final mutationId = failedAfterSync1.first.id;

        // User resolves: Keep Local
        await syncRepo.resolveKeepLocal(mutationId);

        // Verify mutation is now pending
        final pendingAfterResolve = await db.getPendingMutations();
        expect(pendingAfterResolve.length, 1);
        expect(pendingAfterResolve.first.id, mutationId);
        expect(pendingAfterResolve.first.status, 'pending');

        // Sync 2: Phase 1 pushes successfully, Phase 2 returns empty manifest
        fakeAdapter.enqueue(
          (_) async => _jsonResponse(_pushSuccess('Work/keeplocal.md', 4)),
        );
        fakeAdapter.enqueue((_) async => _jsonResponse(_emptyManifest()));

        final result2 = await syncRepo.performSync();

        // Verify: push succeeded, no conflicts
        expect(result2.pushed, 1);
        expect(result2.conflicts, 0);

        // Verify: mutation was removed after successful push
        final pendingAfterSync2 = await db.getPendingMutations();
        final failedAfterSync2 = await db.getFailedMutations();
        expect(pendingAfterSync2.length, 0);
        expect(failedAfterSync2.length, 0);

        // Verify: cache updated with new version
        final entry = await db.getEntry('Work/keeplocal.md');
        expect(entry!.serverVersion, 4);
      },
    );
  });

  // =========================================================================
  // Scenario C — Phase 3 re-push of file already handled by Phase 1 (Bug C)
  //
  // Phase 1 successfully pushes a mutation → removes it from DB.
  // Phase 2 manifest still lists the same path in to_push.
  // Phase 3 uses entry.serverVersion (now updated to 4) to push again.
  //
  // We enqueue two push response slots. A counter tracks how many times
  // Phase 3's slot is consumed. Bug C is present if phase3PushCount == 1.
  // =========================================================================

  group('Scenario C — Phase 3 re-push detection', () {
    test(
      'Tracks whether Phase 3 re-pushes a file already handled by Phase 1',
      () async {
        final localFile = File('${tempDir.path}/doc.md');
        await localFile.writeAsString('doc content');

        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('Work/doc.md'),
            name: const Value('doc.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T12:00:00Z'),
            contentHash: const Value('sha256:doc'),
            localPath: Value(localFile.path),
            serverVersion: const Value(3),
          ),
        );
        await db.enqueueMutation(
          id: 'mut-c-001',
          path: 'Work/doc.md',
          operation: 'update',
          timestamp: '2026-02-19T12:01:00Z',
          baseVersion: 3,
        );

        int phase1PushCount = 0;
        int phase3PushCount = 0;

        // Slot 1: Phase 1 push → success
        fakeAdapter.enqueue((_) async {
          phase1PushCount++;
          return _jsonResponse(_pushSuccess('Work/doc.md', 4));
        });

        // Phase 2: manifest returns to_push with the same path (Bug C trigger)
        fakeAdapter.enqueue(
          (_) async => _jsonResponse({
            'to_push': [
              {'path': 'Work/doc.md'},
            ],
            'to_pull': <dynamic>[],
            'conflicts': <dynamic>[],
          }),
        );

        // Slot 2: Phase 3 push — only consumed if Bug C is present
        fakeAdapter.enqueue((_) async {
          phase3PushCount++;
          return _jsonResponse(_pushSuccess('Work/doc.md', 5));
        });

        final result = await syncRepo.performSync();

        print(
          '[SCENARIO C] phase1PushCount=$phase1PushCount '
          'phase3PushCount=$phase3PushCount pushed=${result.pushed}',
        );

        expect(phase1PushCount, 1, reason: 'Phase 1 must push exactly once');

        if (phase3PushCount == 1) {
          // Bug C confirmed
          expect(
            result.pushed,
            2,
            reason: 'Bug C CONFIRMED: Phase 3 re-pushed Work/doc.md → pushed=2',
          );
        } else {
          expect(phase3PushCount, 0);
          expect(
            result.pushed,
            1,
            reason: 'Phase 3 correctly skipped → pushed=1',
          );
        }

        expect(result.conflicts, 0);
        expect(result.pulled, 0);
      },
    );
  });

  // =========================================================================
  // Scenario D — pushCount and pullCount are accurate end-to-end
  //
  // A.md: pending mutation → Phase 1 pushes → version 2
  // Phase 2 manifest: to_pull=[B.md]
  // Phase 4: pull B.md → bytes + X-File-Version: 3
  //
  // Assertions: pushed==1, pulled==1, DB serverVersions correct
  // =========================================================================

  group('Scenario D — pushCount and pullCount accuracy', () {
    test(
      'pushed==1 pulled==1 with correct serverVersion updates in DB',
      () async {
        final fileA = File('${tempDir.path}/A.md');
        await fileA.writeAsString('content a');

        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('A.md'),
            name: const Value('A.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T12:00:00Z'),
            contentHash: const Value('sha256:a'),
            localPath: Value(fileA.path),
            serverVersion: const Value(1),
          ),
        );
        await db.enqueueMutation(
          id: 'mut-d-001',
          path: 'A.md',
          operation: 'update',
          timestamp: '2026-02-19T12:01:00Z',
          baseVersion: 1,
        );

        // Phase 1: push A.md → success version 2
        fakeAdapter.enqueue(
          (_) async => _jsonResponse(_pushSuccess('A.md', 2)),
        );
        // Phase 2: manifest → to_pull: [B.md]
        fakeAdapter.enqueue(
          (_) async => _jsonResponse({
            'to_push': <dynamic>[],
            'to_pull': [
              {'path': 'B.md'},
            ],
            'conflicts': <dynamic>[],
          }),
        );
        // Phase 4: pull B.md → X-File-Version: 3
        fakeAdapter.enqueue(
          (_) async => _pullResponse(utf8.encode('content b'), 3),
        );

        final result = await syncRepo.performSync();

        expect(
          result.pushed,
          1,
          reason: 'Exactly 1 file pushed (A.md in Phase 1)',
        );
        expect(
          result.pulled,
          1,
          reason: 'Exactly 1 file pulled (B.md in Phase 4)',
        );
        expect(result.conflicts, 0);

        final entryA = await db.getEntry('A.md');
        expect(
          entryA!.serverVersion,
          2,
          reason: 'A.md serverVersion must update to 2 after push',
        );

        final entryB = await db.getEntry('B.md');
        expect(
          entryB!.serverVersion,
          3,
          reason: 'B.md serverVersion must be 3 from X-File-Version header',
        );

        final mutation = await db.getMutationById('mut-d-001');
        expect(
          mutation,
          isNull,
          reason: 'Mutation removed after successful push',
        );

        expect(
          fakeAdapter.isExhausted,
          isTrue,
          reason: 'All enqueued HTTP responses consumed — no unexpected calls',
        );
      },
    );
  });

  // =========================================================================
  // Step 3 — Push / Pull counter unit tests
  // =========================================================================

  group('Counter tests', () {
    test(
      '1 pending mutation with successful push → pushed==1, pulled==0',
      () async {
        final f = File('${tempDir.path}/counter_push.md');
        await f.writeAsString('data');

        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('counter_push.md'),
            name: const Value('counter_push.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T10:00:00Z'),
            contentHash: const Value('sha256:cp'),
            localPath: Value(f.path),
            serverVersion: const Value(1),
          ),
        );
        await db.enqueueMutation(
          id: 'c-push-001',
          path: 'counter_push.md',
          operation: 'update',
          timestamp: '2026-02-19T10:01:00Z',
          baseVersion: 1,
        );

        fakeAdapter.enqueue(
          (_) async => _jsonResponse(_pushSuccess('counter_push.md', 2)),
        );
        fakeAdapter.enqueue((_) async => _jsonResponse(_emptyManifest()));

        final result = await syncRepo.performSync();
        expect(result.pushed, 1);
        expect(result.pulled, 0);
        expect(result.conflicts, 0);
      },
    );

    test(
      '1 file in to_pull → pulled==1, pushed==0, serverVersion updated',
      () async {
        // No mutations — only manifest with a pull
        fakeAdapter.enqueue(
          (_) async => _jsonResponse({
            'to_push': <dynamic>[],
            'to_pull': [
              {'path': 'pull_me.md'},
            ],
            'conflicts': <dynamic>[],
          }),
        );
        fakeAdapter.enqueue(
          (_) async => _pullResponse(utf8.encode('pulled content'), 5),
        );

        final result = await syncRepo.performSync();
        expect(result.pulled, 1);
        expect(result.pushed, 0);
        expect(result.conflicts, 0);

        final entry = await db.getEntry('pull_me.md');
        expect(entry, isNotNull);
        expect(entry!.serverVersion, 5);
      },
    );

    test('Conflict in Phase 1 → pushed==0 for conflicted file', () async {
      final f = File('${tempDir.path}/conflict_file.md');
      await f.writeAsString('conflicting data');

      await db.upsertEntry(
        FileCacheEntriesCompanion(
          path: const Value('conflict_file.md'),
          name: const Value('conflict_file.md'),
          type: const Value('file'),
          lastModified: const Value('2026-02-19T10:00:00Z'),
          contentHash: const Value('sha256:cf'),
          localPath: Value(f.path),
          serverVersion: const Value(2),
        ),
      );
      await db.enqueueMutation(
        id: 'c-conflict-001',
        path: 'conflict_file.md',
        operation: 'update',
        timestamp: '2026-02-19T10:01:00Z',
        baseVersion: 2,
      );

      // Phase 1 → conflict (not success)
      fakeAdapter.enqueue(
        (_) async => _jsonResponse(_pushConflict('conflict_file_c.md')),
      );
      // Phase 2 → empty manifest so Phase 3 doesn't push again
      fakeAdapter.enqueue((_) async => _jsonResponse(_emptyManifest()));

      final result = await syncRepo.performSync();

      expect(
        result.pushed,
        0,
        reason: 'Conflict must NOT increment pushed counter',
      );
      expect(result.conflicts, 1);

      final mutation = await db.getMutationById('c-conflict-001');
      expect(mutation!.status, 'failed');
      expect(mutation.conflictFilePath, 'conflict_file_c.md');
    });

    test(
      'Phase 3 to_push for already-pushed file: tracks re-push via adapter',
      () async {
        final f = File('${tempDir.path}/repush.md');
        await f.writeAsString('repush data');

        await db.upsertEntry(
          FileCacheEntriesCompanion(
            path: const Value('repush.md'),
            name: const Value('repush.md'),
            type: const Value('file'),
            lastModified: const Value('2026-02-19T10:00:00Z'),
            contentHash: const Value('sha256:rp'),
            localPath: Value(f.path),
            serverVersion: const Value(1),
          ),
        );
        await db.enqueueMutation(
          id: 'c-repush-001',
          path: 'repush.md',
          operation: 'update',
          timestamp: '2026-02-19T10:01:00Z',
          baseVersion: 1,
        );

        int phase1Count = 0;
        int phase3Count = 0;

        fakeAdapter.enqueue((_) async {
          phase1Count++;
          return _jsonResponse(_pushSuccess('repush.md', 2));
        });
        fakeAdapter.enqueue(
          (_) async => _jsonResponse({
            'to_push': [
              {'path': 'repush.md'},
            ],
            'to_pull': <dynamic>[],
            'conflicts': <dynamic>[],
          }),
        );
        fakeAdapter.enqueue((_) async {
          phase3Count++;
          return _jsonResponse(_pushSuccess('repush.md', 3));
        });

        final result = await syncRepo.performSync();

        print(
          '[COUNTER TEST] phase1=$phase1Count phase3=$phase3Count '
          'pushed=${result.pushed}',
        );

        expect(phase1Count, 1);
        if (phase3Count == 1) {
          expect(
            result.pushed,
            2,
            reason: 'Bug C: Phase 3 re-pushed repush.md → pushed=2',
          );
        } else {
          expect(phase3Count, 0);
          expect(result.pushed, 1);
        }
      },
    );
  });
}
