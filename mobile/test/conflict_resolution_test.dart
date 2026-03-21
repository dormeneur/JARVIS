// needed for Value
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
  // Schema v5 — localContentSnapshot column
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - Schema v5', () {
    test(
      'MutationQueue has localContentSnapshot column (nullable, defaults null)',
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
        expect(mutation!.localContentSnapshot, isNull);
      },
    );

    test(
      'markMutationAsConflict stores local snapshot & marks failed',
      () async {
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/test.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 3,
        );

        await db.markMutationAsConflict('mut-001', 'Hello from mobile!', 5);

        final mutation = await db.getMutationById('mut-001');
        expect(mutation!.status, 'failed');
        expect(mutation.localContentSnapshot, 'Hello from mobile!');
        expect(mutation.baseVersion, 5);
      },
    );

    test(
      'markMutationAsConflict does not affect mutations for other files',
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

        await db.markMutationAsConflict('mut-001', 'snapshot', 3);

        final mutB = await db.getMutationById('mut-002');
        expect(mutB!.status, 'pending');
        expect(mutB.localContentSnapshot, isNull);
      },
    );
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
      await db.markMutationAsConflict('mut-001', 'snapshot', 3);

      // Resolve: update base version to 5
      await db.updateMutationBaseVersion('mut-001', 5);

      final mutation = await db.getMutationById('mut-001');
      expect(mutation!.status, 'pending');
      expect(mutation.baseVersion, 5);
      expect(mutation.conflictFilePath, isNull); // cleared on resolution
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

      await db.markMutationAsConflict('mut-001', 'local content', 2);

      final failed = await db.getFailedMutations();
      expect(failed.length, 1);
      expect(failed[0].id, 'mut-001');
      expect(failed[0].localContentSnapshot, 'local content');
    });

    test('failed mutations disappear after removal', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'notes/test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      await db.markMutationAsConflict('mut-001', 'snapshot', 2);

      var failed = await db.getFailedMutations();
      expect(failed.length, 1);

      await db.removeMutation('mut-001');

      failed = await db.getFailedMutations();
      expect(failed, isEmpty);
    });

    test(
      'updateMutationBaseVersion moves conflict mutation to pending',
      () async {
        await db.enqueueMutation(
          id: 'mut-001',
          path: 'notes/test.md',
          operation: 'update',
          timestamp: '2026-02-19T12:00:00Z',
          baseVersion: 2,
        );
        await db.markMutationAsConflict('mut-001', 'snapshot', 2);

        await db.updateMutationBaseVersion('mut-001', 4);

        final pending = await db.getPendingMutations();
        final failed = await db.getFailedMutations();

        expect(pending.length, 1);
        expect(pending[0].baseVersion, 4);
        expect(failed, isEmpty);
      },
    );
  });

  // ---------------------------------------------------------------------------
  // Backward compatibility
  // ---------------------------------------------------------------------------

  group('Conflict Resolution - Backward Compatibility', () {
    test(
      'markMutationFailed still works with null localContentSnapshot',
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
        expect(mutation.localContentSnapshot, isNull);
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
