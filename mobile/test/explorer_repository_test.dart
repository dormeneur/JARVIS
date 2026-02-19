import 'package:drift/drift.dart' hide isNull, isNotNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

void main() {
  late AppDatabase db;
  late ExplorerRepository repo;

  setUp(() {
    db = AppDatabase.forTesting(NativeDatabase.memory());
    repo = ExplorerRepository(db: db);
  });

  tearDown(() async {
    await db.close();
  });

  Future<void> seedFiles() async {
    final files = [
      ('readme.md', 'readme.md'),
      ('Personal/notes.md', 'notes.md'),
      ('Personal/diary.md', 'diary.md'),
      ('Work/project.md', 'project.md'),
      ('Work/Deep/file.md', 'file.md'),
    ];
    for (final (path, name) in files) {
      await db.upsertEntry(
        FileCacheEntriesCompanion(
          path: Value(path),
          name: Value(name),
          type: const Value('file'),
          lastModified: const Value('2026-01-01T00:00:00Z'),
          contentHash: const Value('sha256:test'),
        ),
      );
    }
  }

  group('listDirectory', () {
    test('root lists top-level files and inferred dirs', () async {
      await seedFiles();
      final entries = await repo.listDirectory('');

      final names = entries.map((e) => e.name).toList();
      expect(names, contains('Personal'));
      expect(names, contains('Work'));
      expect(names, contains('readme.md'));
      // Directories should come first
      expect(entries.first.isDirectory, true);
    });

    test('subdirectory lists its children', () async {
      await seedFiles();
      final entries = await repo.listDirectory('Personal');

      final names = entries.map((e) => e.name).toList();
      expect(names, contains('notes.md'));
      expect(names, contains('diary.md'));
      expect(names.length, 2);
    });

    test('nested directory works', () async {
      await seedFiles();
      final entries = await repo.listDirectory('Work');

      final names = entries.map((e) => e.name).toList();
      expect(names, contains('Deep'));
      expect(names, contains('project.md'));
    });

    test('empty directory returns empty', () async {
      final entries = await repo.listDirectory('');
      expect(entries, isEmpty);
    });
  });

  group('CRUD operations', () {
    test('upsertFile and getEntry', () async {
      await repo.upsertFile(
        const FileEntry(
          path: 'new.md',
          name: 'new.md',
          type: 'file',
          lastModified: '2026-02-01T00:00:00Z',
          contentHash: 'sha256:xyz',
        ),
      );

      final entry = await repo.getEntry('new.md');
      expect(entry, isNotNull);
      expect(entry!.contentHash, 'sha256:xyz');
    });

    test('removeEntry removes file', () async {
      await repo.upsertFile(
        const FileEntry(
          path: 'trash.md',
          name: 'trash.md',
          type: 'file',
          lastModified: '2026-01-01T00:00:00Z',
        ),
      );

      await repo.removeEntry('trash.md');
      final entry = await repo.getEntry('trash.md');
      expect(entry, isNull);
    });

    test('getAllFiles returns all', () async {
      await seedFiles();
      final all = await repo.getAllFiles();
      expect(all.length, 5);
    });

    test('clearAll empties cache', () async {
      await seedFiles();
      await repo.clearAll();
      final all = await repo.getAllFiles();
      expect(all, isEmpty);
    });

    test('deleteFile removes entry and enqueues mutation', () async {
      // Add a file without local path (to avoid file system operations in test)
      await repo.upsertFile(
        const FileEntry(
          path: 'to_delete.md',
          name: 'to_delete.md',
          type: 'file',
          lastModified: '2026-01-01T00:00:00Z',
          contentHash: 'sha256:test',
        ),
      );

      // Delete the file
      await repo.deleteFile('to_delete.md');

      // Verify entry is removed from cache
      final entry = await repo.getEntry('to_delete.md');
      expect(entry, isNull);

      // Verify delete mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.length, 1);
      expect(mutations[0].path, 'to_delete.md');
      expect(mutations[0].operation, 'delete');
      expect(mutations[0].status, 'pending');
    });
  });
}
