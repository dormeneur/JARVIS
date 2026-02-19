import 'dart:io';

import 'package:drift/drift.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

/// Repository for browsing the vault file tree using local SQLite cache.
class ExplorerRepository {
  final AppDatabase _db;

  ExplorerRepository({required AppDatabase db}) : _db = db;

  /// List entries in a directory. Directories are inferred from file paths.
  Future<List<FileEntry>> listDirectory(String dirPath) async {
    final allFiles = await _db.getAllFiles();

    final prefix = dirPath.isEmpty ? '' : '$dirPath/';
    final entries = <String, FileEntry>{};

    for (final file in allFiles) {
      if (prefix.isNotEmpty && !file.path.startsWith(prefix)) continue;

      final remainder = prefix.isEmpty
          ? file.path
          : file.path.substring(prefix.length);
      final slashIndex = remainder.indexOf('/');

      if (slashIndex == -1) {
        // Direct child file
        entries[file.path] = FileEntry(
          path: file.path,
          name: file.name,
          type: 'file',
          sizeBytes: file.sizeBytes,
          lastModified: file.lastModified,
          contentHash: file.contentHash,
          localPath: file.localPath,
          lastSynced: file.lastSynced,
          serverVersion: file.serverVersion,
        );
      } else {
        // Directory inferred from path
        final dirName = remainder.substring(0, slashIndex);
        final fullDirPath = prefix.isEmpty ? dirName : '$prefix$dirName';
        if (!entries.containsKey(fullDirPath)) {
          entries[fullDirPath] = FileEntry(
            path: fullDirPath,
            name: dirName,
            type: 'directory',
            lastModified: file.lastModified,
            serverVersion: file.serverVersion,
          );
        }
      }
    }

    // Sort: directories first, then alphabetical
    final result = entries.values.toList();
    result.sort((a, b) {
      if (a.isDirectory && !b.isDirectory) return -1;
      if (!a.isDirectory && b.isDirectory) return 1;
      return a.name.toLowerCase().compareTo(b.name.toLowerCase());
    });

    return result;
  }

  /// Upsert a file entry into the cache.
  Future<void> upsertFile(FileEntry entry) async {
    await _db.upsertEntry(
      FileCacheEntriesCompanion(
        path: Value(entry.path),
        name: Value(entry.name),
        type: Value(entry.type),
        sizeBytes: Value(entry.sizeBytes),
        lastModified: Value(entry.lastModified),
        contentHash: Value(entry.contentHash),
        localPath: Value(entry.localPath),
        lastSynced: Value(entry.lastSynced),
        serverVersion: Value(entry.serverVersion),
      ),
    );
  }

  /// Remove a stale entry.
  Future<void> removeEntry(String path) async {
    await _db.deleteEntry(path);
  }

  /// Delete a file locally (remove from SQLite and delete local file).
  /// Enqueues a delete mutation for sync.
  Future<void> deleteFile(String path) async {
    // Get entry to find local file and base_version
    final entry = await _db.getEntry(path);
    final baseVersion = entry?.serverVersion ?? 1;
    
    // Delete local file if it exists
    if (entry?.localPath != null) {
      final file = File(entry!.localPath!);
      if (file.existsSync()) {
        file.deleteSync();
      }
    }
    
    // Remove from SQLite
    await _db.deleteEntry(path);
    
    // Enqueue delete mutation with base_version
    await _db.enqueueMutation(
      id: 'del-${DateTime.now().millisecondsSinceEpoch}-${path.hashCode}',
      path: path,
      operation: 'delete',
      timestamp: DateTime.now().toUtc().toIso8601String(),
      baseVersion: baseVersion,
    );
  }

  /// Get a single entry by path.
  Future<FileEntry?> getEntry(String path) async {
    final row = await _db.getEntry(path);
    if (row == null) return null;
    return FileEntry(
      path: row.path,
      name: row.name,
      type: row.type,
      sizeBytes: row.sizeBytes,
      lastModified: row.lastModified,
      contentHash: row.contentHash,
      localPath: row.localPath,
      lastSynced: row.lastSynced,
      serverVersion: row.serverVersion,
    );
  }

  /// Get all file entries (for building manifest).
  Future<List<FileEntry>> getAllFiles() async {
    final rows = await _db.getAllFiles();
    return rows
        .map(
          (r) => FileEntry(
            path: r.path,
            name: r.name,
            type: r.type,
            sizeBytes: r.sizeBytes,
            lastModified: r.lastModified,
            contentHash: r.contentHash,
            localPath: r.localPath,
            lastSynced: r.lastSynced,
            serverVersion: r.serverVersion,
          ),
        )
        .toList();
  }

  /// Clear all cached entries.
  Future<void> clearAll() async {
    await _db.deleteAllEntries();
  }
}
