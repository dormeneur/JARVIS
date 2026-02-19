import 'package:drift/drift.dart' hide isNull, isNotNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';

void main() {
  late AppDatabase db;

  setUp(() {
    db = AppDatabase.forTesting(NativeDatabase.memory());
  });

  tearDown(() async {
    await db.close();
  });

  // ---------------------------------------------------------------------------
  // Schema v4 — conflictFilePath column
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - Schema v4', () {
    test(
      'MutationQueue has conflictFilePath column (nullable, defaults null)',
      () async {
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/test.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 1,
        );

        final mutation = await db.getMutationById('mut-001');
        expect(mutation, isNotNull);
        expect(mutation!.conflictFilePath, isNull);
      },
    );

    test(
      'markMutationConflict stores conflict file path and marks failed',
      () async {
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/test.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 3,
        );

        await db.markMutationConflict(
          'mut-001',
          'notes/test_conflict_1708000000.md',
        );

        final mutation = await db.getMutationById('mut-001');
        expect(mutation!.status, 'failed');
        expect(mutation.conflictFilePath, 'notes/test_conflict_1708000000.md');
        expect(mutation.retryCount, 1);
      },
    );

    test(
      'markMutationConflict does not affect mutations for other files',
      () async {
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/a.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 1,
        );
        await db.enqueueMutation(
          id: 'mut-002',
          path: 'notes/b.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:01Z',
          baseVersion: 2,
        );

        await db.markMutationConflict('mut-001', 'notes/a_conflict_123.md');

        final mutB = await db.getMutationById('mut-002');
        expect(mutB!.status, 'pending');
        expect(mutB.conflictFilePath, isNull);
      },
    );

    test('markMutationConflict on non-existent id is a no-op', () async {
      // Should not throw
      await expectLater(
        db.markMutationConflict('no-such-id', 'conflict.md'),
        completes,
      );
    });
  });

  // ---------------------------------------------------------------------------
  // updateMutationBaseVersion
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - updateMutationBaseVersion', () {
    test('updates baseVersion and resets status to pending', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'notes/test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 3,
      );
      await db.markMutationConflict('mut-001', 'notes/test_conflict_123.md');

      // Resolve: update base version to 5 (server's current version)
      await db.updateMutationBaseVersion('mut-001', 5);

      final mutation = await db.getMutationById('mut-001');
      expect(mutation!.status, 'pending');
      expect(mutation.baseVersion, 5);
      expect(mutation.conflictFilePath, isNull); // cleared on resolution
    });

    test('keeps original retryCount after updateMutationBaseVersion', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'notes/test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 2,
      );
      await db.markMutationConflict('mut-001', 'notes/test_conflict_123.md');
      // retry count is now 1

      await db.updateMutationBaseVersion('mut-001', 4);

      final mutation = await db.getMutationById('mut-001');
      expect(mutation!.retryCount, 1); // not reset
      expect(mutation.status, 'pending');
    });
  });

  // ---------------------------------------------------------------------------
  // getMutationById
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - getMutationById', () {
    test('returns null for unknown id', () async {
      final result = await db.getMutationById('nonexistent');
      expect(result, isNull);
    });

    test('returns mutation for known id', () async {
      await db.enqueueMutation(
        id: 'mut-abc',
        path: 'notes/test.md',
        operation: 'create',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      final result = await db.getMutationById('mut-abc');
      expect(result, isNotNull);
      expect(result!.id, 'mut-abc');
      expect(result.operation, 'create');
    });
  });

  // ---------------------------------------------------------------------------
  // watchFailedMutations stream
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - watchFailedMutations stream', () {
    test('emits empty list when no failed mutations', () async {
      final stream = db.watchFailedMutations();
      final first = await stream.first;
      expect(first, isEmpty);
    });

    test('emits mutations when they become failed', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'notes/test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      await db.markMutationConflict('mut-001', 'notes/test_conflict_1.md');

      final failed = await db.getFailedMutations();
      expect(failed.length, 1);
      expect(failed[0].id, 'mut-001');
      expect(failed[0].conflictFilePath, 'notes/test_conflict_1.md');
    });

    test('failed mutations disappear after removal', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'notes/test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      await db.markMutationConflict('mut-001', 'notes/test_conflict_1.md');

      var failed = await db.getFailedMutations();
      expect(failed.length, 1);

      await db.removeMutation('mut-001');

      failed = await db.getFailedMutations();
      expect(failed, isEmpty);
    });

    test(
      'resolveKeepLocal-equivalent: updateMutationBaseVersion moves mutation to pending',
      () async {
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/test.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 2,
        );
        await db.markMutationConflict('mut-001', 'notes/test_conflict.md');

        // Simulate resolveKeepLocal: fetch server version (4) + update
        await db.updateMutationBaseVersion('mut-001', 4);

        final pending = await db.getPendingMutations();
        final failed = await db.getFailedMutations();

        expect(pending.length, 1);
        expect(pending[0].baseVersion, 4);
        expect(pending[0].conflictFilePath, isNull);
        expect(failed, isEmpty);
      },
    );
  });

  // ---------------------------------------------------------------------------
  // v4 Migration — existing data preserved
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - Schema v4 Migration', () {
    test(
      'v4 schema: existing mutations without conflict still have null conflictFilePath',
      () async {
        // Create mutations with the normal flow (no conflict)
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/a.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 1,
        );
        await db.enqueueMutation(
          id: 'mut-002',
          path: 'notes/b.md',
          operation: 'create',
          timestamp: '2026-02-19T12:00:01Z',
          baseVersion: 1,
        );

        final pending = await db.getPendingMutations();
        expect(pending.length, 2);
        for (final m in pending) {
          expect(m.conflictFilePath, isNull);
        }
      },
    );

    test('v4 schema: file cache data preserved alongside new column', () async {
      // Upsert file cache entry
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('docs/readme.md'),
          name: Value('readme.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T10:00:00Z'),
          serverVersion: Value(7),
        ),
      );

      // Enqueue mutation for it
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'docs/readme.md',
        operation: 'update',
        timestamp: '2026-02-19T10:01:00Z',
        baseVersion: 7,
      );

      // Mark conflict
      await db.markMutationConflict(
        'mut-001',
        'docs/readme_conflict_1708000000.md',
      );

      // Verify file cache is intact
      final entry = await db.getEntry('docs/readme.md');
      expect(entry!.serverVersion, 7);

      // Verify mutation has conflict stored
      final mutation = await db.getMutationById('mut-001');
      expect(mutation!.conflictFilePath, 'docs/readme_conflict_1708000000.md');
      expect(mutation.status, 'failed');
      expect(mutation.baseVersion, 7);
    });
  });

  // ---------------------------------------------------------------------------
  // Backward compatibility — markMutationFailed still works (no conflictFilePath)
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - Backward Compatibility', () {
    test(
      'markMutationFailed still sets status=failed with null conflictFilePath',
      () async {
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/test.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 1,
        );

        await db.markMutationFailed('mut-001');

        final mutation = await db.getMutationById('mut-001');
        expect(mutation!.status, 'failed');
        expect(mutation.conflictFilePath, isNull);
      },
    );

    test('resetMutation preserves baseVersion and clears status', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'notes/test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 9,
      );
      await db.markMutationFailed('mut-001');
      await db.resetMutation('mut-001');

      final mutation = await db.getMutationById('mut-001');
      expect(mutation!.status, 'pending');
      expect(mutation.baseVersion, 9);
    });
  });
}
