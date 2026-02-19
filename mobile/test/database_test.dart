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

  group('FileCacheEntries CRUD', () {
    test('upsertEntry and getEntry', () async {
      await db.upsertEntry(
        FileCacheEntriesCompanion(
          path: const Value('readme.md'),
          name: const Value('readme.md'),
          type: const Value('file'),
          sizeBytes: const Value(1024),
          lastModified: const Value('2026-01-15T12:00:00Z'),
          contentHash: const Value('sha256:abc123'),
          localPath: const Value('/data/mirror/readme.md'),
          lastSynced: const Value('2026-01-15T12:05:00Z'),
        ),
      );

      final entry = await db.getEntry('readme.md');
      expect(entry, isNotNull);
      expect(entry!.name, 'readme.md');
      expect(entry.contentHash, 'sha256:abc123');
      expect(entry.lastModified, '2026-01-15T12:00:00Z');
    });

    test('upsert updates existing entry', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('a.md'),
          name: Value('a.md'),
          type: Value('file'),
          lastModified: Value('2026-01-01T00:00:00Z'),
          contentHash: Value('sha256:old'),
        ),
      );

      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('a.md'),
          name: Value('a.md'),
          type: Value('file'),
          lastModified: Value('2026-02-01T00:00:00Z'),
          contentHash: Value('sha256:new'),
        ),
      );

      final entry = await db.getEntry('a.md');
      expect(entry!.contentHash, 'sha256:new');
    });

    test('deleteEntry removes entry', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('todelete.md'),
          name: Value('todelete.md'),
          type: Value('file'),
          lastModified: Value('2026-01-01T00:00:00Z'),
        ),
      );

      await db.deleteEntry('todelete.md');
      final entry = await db.getEntry('todelete.md');
      expect(entry, isNull);
    });

    test('getAllFiles returns all entries', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('a.md'),
          name: Value('a.md'),
          type: Value('file'),
          lastModified: Value('2026-01-01T00:00:00Z'),
        ),
      );
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('b.md'),
          name: Value('b.md'),
          type: Value('file'),
          lastModified: Value('2026-01-01T00:00:00Z'),
        ),
      );

      final all = await db.getAllFiles();
      expect(all.length, 2);
    });

    test('deleteAllEntries clears table', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('x.md'),
          name: Value('x.md'),
          type: Value('file'),
          lastModified: Value('2026-01-01T00:00:00Z'),
        ),
      );

      await db.deleteAllEntries();
      final all = await db.getAllFiles();
      expect(all, isEmpty);
    });

    test('getEntry returns null for non-existent', () async {
      final entry = await db.getEntry('nope.md');
      expect(entry, isNull);
    });
  });

  group('Directory path inference', () {
    test('getAllDirectoryPaths infers dirs from file paths', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('Personal/notes.md'),
          name: Value('notes.md'),
          type: Value('file'),
          lastModified: Value('2026-01-01T00:00:00Z'),
        ),
      );
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('Work/Deep/file.md'),
          name: Value('file.md'),
          type: Value('file'),
          lastModified: Value('2026-01-01T00:00:00Z'),
        ),
      );

      final dirs = await db.getAllDirectoryPaths();
      expect(dirs, contains('Personal'));
      expect(dirs, contains('Work'));
      expect(dirs, contains('Work/Deep'));
    });
  });

  group('MutationQueue operations', () {
    test('enqueueMutation adds mutation to queue', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'create',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      final mutations = await db.getPendingMutations();
      expect(mutations.length, 1);
      expect(mutations[0].id, 'mut-001');
      expect(mutations[0].path, 'test.md');
      expect(mutations[0].operation, 'create');
      expect(mutations[0].status, 'pending');
      expect(mutations[0].retryCount, 0);
      expect(mutations[0].baseVersion, 1);
    });

    test('getPendingMutations returns only pending', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'a.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );
      await db.enqueueMutation(
        id: 'mut-002',
        path: 'b.md',
        operation: 'delete',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 1,
      );

      // Mark one as failed
      await db.markMutationFailed('mut-001');

      final pending = await db.getPendingMutations();
      expect(pending.length, 1);
      expect(pending[0].id, 'mut-002');
    });

    test('getPendingMutations orders by timestamp', () async {
      await db.enqueueMutation(
        id: 'mut-003',
        path: 'c.md',
        operation: 'create',
        timestamp: '2026-02-19T12:03:00Z',
        baseVersion: 1,
      );
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'a.md',
        operation: 'create',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 1,
      );
      await db.enqueueMutation(
        id: 'mut-002',
        path: 'b.md',
        operation: 'create',
        timestamp: '2026-02-19T12:02:00Z',
        baseVersion: 1,
      );

      final pending = await db.getPendingMutations();
      expect(pending.length, 3);
      expect(pending[0].id, 'mut-001');
      expect(pending[1].id, 'mut-002');
      expect(pending[2].id, 'mut-003');
    });

    test('removeMutation deletes from queue', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'create',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      await db.removeMutation('mut-001');

      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('markMutationFailed updates status and increments retry', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      await db.markMutationFailed('mut-001');

      final failed = await db.getFailedMutations();
      expect(failed.length, 1);
      expect(failed[0].status, 'failed');
      expect(failed[0].retryCount, 1);

      // Mark failed again
      await db.markMutationFailed('mut-001');
      final failedAgain = await db.getFailedMutations();
      expect(failedAgain[0].retryCount, 2);
    });

    test('resetMutation changes failed back to pending', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'delete',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      await db.markMutationFailed('mut-001');
      await db.resetMutation('mut-001');

      final pending = await db.getPendingMutations();
      expect(pending.length, 1);
      expect(pending[0].status, 'pending');
    });

    test('getPendingMutationCount returns correct count', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'a.md',
        operation: 'create',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );
      await db.enqueueMutation(
        id: 'mut-002',
        path: 'b.md',
        operation: 'update',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 1,
      );

      final count = await db.getPendingMutationCount();
      expect(count, 2);

      await db.markMutationFailed('mut-001');
      final countAfter = await db.getPendingMutationCount();
      expect(countAfter, 1);
    });

    test('clearAllMutations removes all mutations', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'a.md',
        operation: 'create',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );
      await db.enqueueMutation(
        id: 'mut-002',
        path: 'b.md',
        operation: 'delete',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 1,
      );

      await db.clearAllMutations();

      final pending = await db.getPendingMutations();
      expect(pending, isEmpty);
    });

    test('multiple operations on same path allowed', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'create',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );
      await db.enqueueMutation(
        id: 'mut-002',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 2,
      );

      final mutations = await db.getPendingMutations();
      expect(mutations.length, 2);
    });
  });
}
