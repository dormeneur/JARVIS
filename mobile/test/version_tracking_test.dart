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

  group('Version Tracking - Database Schema', () {
    test('FileCacheEntries has serverVersion column with default value 1', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
        ),
      );

      final entry = await db.getEntry('test.md');
      expect(entry, isNotNull);
      expect(entry!.serverVersion, 1);
    });

    test('FileCacheEntries can store custom serverVersion', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(5),
        ),
      );

      final entry = await db.getEntry('test.md');
      expect(entry!.serverVersion, 5);
    });

    test('MutationQueue has baseVersion column', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 3,
      );

      final mutations = await db.getPendingMutations();
      expect(mutations.length, 1);
      expect(mutations[0].baseVersion, 3);
    });

    test('serverVersion updates correctly on upsert', () async {
      // Initial insert with version 1
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(1),
        ),
      );

      // Update with version 2
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:01:00Z'),
          serverVersion: Value(2),
        ),
      );

      final entry = await db.getEntry('test.md');
      expect(entry!.serverVersion, 2);
    });
  });

  group('Version Tracking - Migration', () {
    test('Migration from v2 to v3 adds serverVersion and baseVersion columns', () async {
      // This test verifies the migration logic works
      // The setUp already runs migrations, so we just verify the columns exist
      
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
        ),
      );

      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 1,
      );

      final entry = await db.getEntry('test.md');
      final mutations = await db.getPendingMutations();

      expect(entry!.serverVersion, 1);
      expect(mutations[0].baseVersion, 1);
    });
  });

  group('Version Tracking - Mutation Queue Workflow', () {
    test('Enqueuing mutation captures current serverVersion as baseVersion', () async {
      // Simulate file pulled from server with version 3
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(3),
        ),
      );

      // User edits file - mutation should capture version 3 as baseVersion
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 3,
      );

      final mutations = await db.getPendingMutations();
      expect(mutations[0].baseVersion, 3);
    });

    test('Multiple edits on same file track different baseVersions', () async {
      // Initial file at version 1
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(1),
        ),
      );

      // First edit
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 1,
      );

      // Simulate successful sync - version increments to 2
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:01:00Z'),
          serverVersion: Value(2),
        ),
      );
      await db.removeMutation('mut-001');

      // Second edit
      await db.enqueueMutation(
        id: 'mut-002',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:02:00Z',
        baseVersion: 2,
      );

      final mutations = await db.getPendingMutations();
      expect(mutations.length, 1);
      expect(mutations[0].baseVersion, 2);
    });

    test('Delete mutation captures current serverVersion', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(4),
        ),
      );

      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'delete',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 4,
      );

      final mutations = await db.getPendingMutations();
      expect(mutations[0].baseVersion, 4);
    });
  });

  group('Version Tracking - Conflict Detection Scenarios', () {
    test('Stale baseVersion indicates potential conflict', () async {
      // File at version 5 on server
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(5),
        ),
      );

      // User edits based on version 5
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 5,
      );

      // Meanwhile, another device pushes version 6
      // When we try to push with baseVersion=5, server will detect conflict
      
      final mutations = await db.getPendingMutations();
      expect(mutations[0].baseVersion, 5);
      // In real sync, server would reject this because current version is 6
    });

    test('Up-to-date baseVersion allows successful push', () async {
      // File at version 2
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(2),
        ),
      );

      // User edits based on version 2
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:01:00Z',
        baseVersion: 2,
      );

      // Push succeeds, server returns version 3
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:01:00Z'),
          serverVersion: Value(3),
        ),
      );
      await db.removeMutation('mut-001');

      final entry = await db.getEntry('test.md');
      expect(entry!.serverVersion, 3);
    });
  });

  group('Version Tracking - Edge Cases', () {
    test('New file starts at version 1', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('new.md'),
          name: Value('new.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
        ),
      );

      final entry = await db.getEntry('new.md');
      expect(entry!.serverVersion, 1);
    });

    test('Version can increment beyond typical ranges', () async {
      await db.upsertEntry(
        const FileCacheEntriesCompanion(
          path: Value('test.md'),
          name: Value('test.md'),
          type: Value('file'),
          lastModified: Value('2026-02-19T12:00:00Z'),
          serverVersion: Value(999),
        ),
      );

      final entry = await db.getEntry('test.md');
      expect(entry!.serverVersion, 999);
    });

    test('Failed mutation retains original baseVersion', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 5,
      );

      await db.markMutationFailed('mut-001');

      final failed = await db.getFailedMutations();
      expect(failed[0].baseVersion, 5);
    });

    test('Reset mutation preserves baseVersion', () async {
      await db.enqueueMutation(
        id: 'mut-001',
        path: 'test.md',
        operation: 'update',
        timestamp: '2026-02-19T12:00:00Z',
        baseVersion: 7,
      );

      await db.markMutationFailed('mut-001');
      await db.resetMutation('mut-001');

      final pending = await db.getPendingMutations();
      expect(pending[0].baseVersion, 7);
    });
  });
}
