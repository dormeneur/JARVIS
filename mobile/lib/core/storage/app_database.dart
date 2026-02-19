import 'dart:io';

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:path_provider/path_provider.dart';
import 'package:path/path.dart' as p;

part 'app_database.g.dart';

/// Table for cached file metadata.
class FileCacheEntries extends Table {
  TextColumn get path => text()();
  TextColumn get name => text()();
  TextColumn get type => text()(); // 'file' or 'directory'
  IntColumn get sizeBytes => integer().nullable()();
  TextColumn get lastModified => text()(); // ISO8601 UTC with Z suffix
  TextColumn get contentHash => text().nullable()();
  TextColumn get localPath => text().nullable()();
  TextColumn get lastSynced => text().nullable()(); // ISO8601 UTC with Z suffix

  @override
  Set<Column> get primaryKey => {path};
}

@DriftDatabase(tables: [FileCacheEntries])
class AppDatabase extends _$AppDatabase {
  AppDatabase() : super(_openConnection());

  /// Test constructor for in-memory database.
  AppDatabase.forTesting(super.executor);

  @override
  int get schemaVersion => 1;

  // --- File Cache Operations ---

  /// Get all file entries.
  Future<List<FileCacheEntry>> getAllFiles() => select(fileCacheEntries).get();

  /// Get entries for a specific parent directory path.
  /// For root, pass empty string.
  Future<List<FileCacheEntry>> getEntriesInDirectory(String dirPath) {
    final prefix = dirPath.isEmpty ? '' : '$dirPath/';
    return (select(fileCacheEntries)..where((e) {
          if (prefix.isEmpty) {
            // Root: entries that have no slash in their path
            return e.path.like('%').not() |
                CustomExpression<bool>("path NOT LIKE '%/%'");
          }
          // Match entries that start with prefix but have no additional slashes
          return e.path.like('$prefix%');
        }))
        .get()
        .then((entries) {
          // Filter in Dart for precise directory matching
          return entries.where((e) {
            if (prefix.isEmpty) {
              return !e.path.contains('/');
            }
            final remainder = e.path.substring(prefix.length);
            return !remainder.contains('/');
          }).toList();
        });
  }

  /// Upsert a file cache entry.
  Future<void> upsertEntry(FileCacheEntriesCompanion entry) {
    return into(fileCacheEntries).insertOnConflictUpdate(entry);
  }

  /// Delete a file cache entry by path.
  Future<void> deleteEntry(String path) {
    return (delete(fileCacheEntries)..where((e) => e.path.equals(path))).go();
  }

  /// Delete all entries.
  Future<void> deleteAllEntries() => delete(fileCacheEntries).go();

  /// Get a single entry by path.
  Future<FileCacheEntry?> getEntry(String path) {
    return (select(
      fileCacheEntries,
    )..where((e) => e.path.equals(path))).getSingleOrNull();
  }

  /// Get all distinct parent directory paths (for building tree).
  Future<List<String>> getAllDirectoryPaths() async {
    final entries = await getAllFiles();
    final dirs = <String>{};
    for (final entry in entries) {
      final parts = entry.path.split('/');
      for (var i = 1; i < parts.length; i++) {
        dirs.add(parts.sublist(0, i).join('/'));
      }
    }
    return dirs.toList()..sort();
  }
}

LazyDatabase _openConnection() {
  return LazyDatabase(() async {
    final dbDir = await getApplicationDocumentsDirectory();
    final file = File(p.join(dbDir.path, 'jarvis_cache.db'));
    return NativeDatabase.createInBackground(file);
  });
}
