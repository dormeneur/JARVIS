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
    if (row != null) {
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

    // Try to infer directory if no explicit row was found
    final prefix = path.isEmpty ? '' : '$path/';
    if (prefix.isNotEmpty) {
      final allFiles = await _db.getAllFiles();
      for (final file in allFiles) {
        if (file.path.startsWith(prefix)) {
          return FileEntry(
            path: path,
            name: path.split('/').last,
            type: 'directory',
            lastModified: DateTime.now().toUtc().toIso8601String(),
            serverVersion: 1, // Default server version for inferred folders
          );
        }
      }
    }

    return null;
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

  /// Get all child file paths for a given folder path.
  ///
  /// Returns a list of paths for all direct children (files and folders)
  /// of the specified folder.
  Future<List<String>> getChildrenPaths(String folderPath) async {
    final allFiles = await _db.getAllFiles();
    final prefix = folderPath.isEmpty ? '' : '$folderPath/';
    final children = <String>[];

    for (final file in allFiles) {
      if (prefix.isNotEmpty && !file.path.startsWith(prefix)) continue;

      final remainder = prefix.isEmpty
          ? file.path
          : file.path.substring(prefix.length);

      // Only include direct children (no additional slashes in remainder)
      if (!remainder.contains('/')) {
        children.add(file.path);
      }
    }

    return children;
  }

  /// Get all child file entries for a given folder path.
  ///
  /// Returns a list of FileEntry objects for all direct children (files and folders)
  /// of the specified folder.
  Future<List<FileEntry>> getChildren(String folderPath) async {
    final allFiles = await _db.getAllFiles();
    final prefix = folderPath.isEmpty ? '' : '$folderPath/';
    final children = <FileEntry>[];

    for (final file in allFiles) {
      if (prefix.isNotEmpty && !file.path.startsWith(prefix)) continue;

      final remainder = prefix.isEmpty
          ? file.path
          : file.path.substring(prefix.length);

      // Only include direct children (no additional slashes in remainder)
      if (!remainder.contains('/')) {
        children.add(FileEntry(
          path: file.path,
          name: file.name,
          type: file.type,
          sizeBytes: file.sizeBytes,
          lastModified: file.lastModified,
          contentHash: file.contentHash,
          localPath: file.localPath,
          lastSynced: file.lastSynced,
          serverVersion: file.serverVersion,
        ));
      }
    }

    return children;
  }

  /// Get the total count of all descendants (files and subfolders) for a given folder path.
  /// Used for UI feedback when deleting or moving folders.
  Future<int> getDescendantCount(String folderPath) async {
    final allFiles = await _db.getAllFiles();
    final prefix = folderPath.isEmpty ? '' : '$folderPath/';
    int count = 0;
    for (final file in allFiles) {
      if (prefix.isNotEmpty && file.path.startsWith(prefix)) {
        count++;
      }
    }
    return count;
  }

  /// Filter a list of paths to only include folder paths.
  ///
  /// Returns a list containing only the paths that represent folders
  /// (type == 'directory').
  Future<List<String>> getFolderPaths(List<String> paths) async {
    final folders = <String>[];

    for (final path in paths) {
      final entry = await _db.getEntry(path);
      if (entry != null && entry.type == 'directory') {
        folders.add(path);
      }
    }

    return folders;
  }

  /// Get all file names in a given path.
  ///
  /// Returns a set of file names (not full paths) for all direct children
  /// in the specified folder. Used for checking name conflicts.
  Future<Set<String>> getFileNamesInPath(String path) async {
    final allFiles = await _db.getAllFiles();
    final prefix = path.isEmpty ? '' : '$path/';
    final names = <String>{};

    for (final file in allFiles) {
      if (prefix.isNotEmpty && !file.path.startsWith(prefix)) continue;

      final remainder = prefix.isEmpty
          ? file.path
          : file.path.substring(prefix.length);

      // Only include direct children (no additional slashes in remainder)
      if (!remainder.contains('/')) {
        names.add(file.name);
      }
    }

    return names;
  }


  /// Search for files matching a query across all directories.
  ///
  /// Searches file names case-insensitively, returning matching entries
  /// sorted by relevance (exact match first, then starts-with, then contains).
  Future<List<FileEntry>> searchFiles(String query) async {
    if (query.trim().isEmpty) return [];

    final allFiles = await _db.getAllFiles();
    final lowerQuery = query.toLowerCase();
    final matches = <FileEntry>[];

    for (final file in allFiles) {
      if (file.name.toLowerCase().contains(lowerQuery)) {
        matches.add(FileEntry(
          path: file.path,
          name: file.name,
          type: file.type,
          sizeBytes: file.sizeBytes,
          lastModified: file.lastModified,
          contentHash: file.contentHash,
          localPath: file.localPath,
          lastSynced: file.lastSynced,
          serverVersion: file.serverVersion,
        ));
      }
    }

    // Sort by relevance: exact > starts-with > contains, then alphabetical
    matches.sort((a, b) {
      final aLower = a.name.toLowerCase();
      final bLower = b.name.toLowerCase();
      final aExact = aLower == lowerQuery;
      final bExact = bLower == lowerQuery;
      if (aExact && !bExact) return -1;
      if (!aExact && bExact) return 1;
      final aStarts = aLower.startsWith(lowerQuery);
      final bStarts = bLower.startsWith(lowerQuery);
      if (aStarts && !bStarts) return -1;
      if (!aStarts && bStarts) return 1;
      return aLower.compareTo(bLower);
    });

    return matches;
  }
}
