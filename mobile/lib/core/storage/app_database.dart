import 'dart:io';
import 'dart:developer' as developer;

import 'package:drift/drift.dart';
import 'package:drift/native.dart';
import 'package:path_provider/path_provider.dart';
import 'package:path/path.dart' as p;
import 'package:jarvis_mobile/features/chat/data/chat_messages_table.dart';

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
  IntColumn get serverVersion => integer().withDefault(
    const Constant(1),
  )(); // Server version for conflict detection

  @override
  Set<Column> get primaryKey => {path};
}

/// Table for tracking offline mutations (create/update/delete operations).
class MutationQueue extends Table {
  TextColumn get id => text()(); // UUID
  TextColumn get path => text()();
  TextColumn get operation => text()(); // 'create', 'update', 'delete'
  TextColumn get timestamp => text()(); // ISO8601 UTC with Z suffix
  IntColumn get retryCount => integer().withDefault(const Constant(0))();
  TextColumn get status => text()(); // 'pending', 'failed'
  IntColumn get baseVersion => integer()(); // Client's known server version
  TextColumn get conflictFilePath =>
      text().nullable()(); // DEPRECATED: kept for migration compatibility
  TextColumn get localContentSnapshot => text()
      .nullable()(); // Snapshot of local content at conflict detection time

  @override
  Set<Column> get primaryKey => {id};
}

@DriftDatabase(tables: [FileCacheEntries, MutationQueue, ChatMessages])
class AppDatabase extends _$AppDatabase {
  AppDatabase() : super(_openConnection());

  /// Test constructor for in-memory database.
  AppDatabase.forTesting(super.executor);

  @override
  int get schemaVersion => 6;

  @override
  MigrationStrategy get migration {
    return MigrationStrategy(
      onCreate: (Migrator m) async {
        await m.createAll();
      },
      onUpgrade: (Migrator m, int from, int to) async {
        if (from < 2) {
          await m.createTable(mutationQueue);
        }
        if (from < 3) {
          await m.addColumn(fileCacheEntries, fileCacheEntries.serverVersion);
          await m.addColumn(mutationQueue, mutationQueue.baseVersion);
          await customStatement('''
            UPDATE mutation_queue 
            SET base_version = COALESCE(
              (SELECT server_version FROM file_cache_entries WHERE file_cache_entries.path = mutation_queue.path),
              1
            )
            WHERE base_version IS NULL
          ''');
        }
        if (from < 4) {
          await m.addColumn(mutationQueue, mutationQueue.conflictFilePath);
        }
        if (from < 5) {
          await m.addColumn(mutationQueue, mutationQueue.localContentSnapshot);
        }
        if (from < 6) {
          await m.createTable(chatMessages);
        }
      },
    );
  }

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

  // --- Mutation Queue Operations ---

  /// Enqueue a mutation (create, update, or delete operation).
  Future<void> enqueueMutation({
    required String id,
    required String path,
    required String operation,
    required String timestamp,
    required int baseVersion,
  }) {
    return into(mutationQueue).insert(
      MutationQueueCompanion.insert(
        id: id,
        path: path,
        operation: operation,
        timestamp: timestamp,
        status: 'pending',
        baseVersion: baseVersion,
      ),
    );
  }

  /// Get all pending mutations ordered by timestamp.
  Future<List<MutationQueueData>> getPendingMutations() {
    return (select(mutationQueue)
          ..where((m) => m.status.equals('pending'))
          ..orderBy([(m) => OrderingTerm.asc(m.timestamp)]))
        .get();
  }

  /// Get all failed mutations.
  Future<List<MutationQueueData>> getFailedMutations() {
    return (select(mutationQueue)
          ..where((m) => m.status.equals('failed'))
          ..orderBy([(m) => OrderingTerm.asc(m.timestamp)]))
        .get();
  }

  /// Watch failed mutations as a reactive stream (for UI providers).
  Stream<List<MutationQueueData>> watchFailedMutations() {
    return (select(mutationQueue)
          ..where((m) => m.status.equals('failed'))
          ..orderBy([(m) => OrderingTerm.asc(m.timestamp)]))
        .watch();
  }

  /// Get a single mutation by ID.
  Future<MutationQueueData?> getMutationById(String id) {
    return (select(
      mutationQueue,
    )..where((m) => m.id.equals(id))).getSingleOrNull();
  }

  /// Remove a mutation from the queue by ID.
  Future<void> removeMutation(String id) {
    return (delete(mutationQueue)..where((m) => m.id.equals(id))).go();
  }

  /// Remove all mutations for a specific path.
  Future<void> removeMutationsForPath(String path) {
    return (delete(mutationQueue)..where((m) => m.path.equals(path))).go();
  }

  /// Remove all mutations for a specific folder path prefix (e.g., when deleting a folder).
  Future<void> removeMutationsForPathPrefix(String folderPath) {
    final prefix = folderPath.isEmpty ? '' : '$folderPath/';
    if (prefix.isEmpty) return clearAllMutations();
    
    return (delete(mutationQueue)..where((m) => m.path.like('$prefix%'))).go();
  }

  /// Mark a mutation as failed and increment retry count.
  Future<void> markMutationFailed(String id) async {
    final mutation = await (select(
      mutationQueue,
    )..where((m) => m.id.equals(id))).getSingleOrNull();

    if (mutation != null) {
      await (update(mutationQueue)..where((m) => m.id.equals(id))).write(
        MutationQueueCompanion(
          status: const Value('failed'),
          retryCount: Value(mutation.retryCount + 1),
        ),
      );
    }
  }

  /// Mark a mutation as failed due to a version conflict, storing
  /// the local content snapshot for the conflict UI.
  Future<void> markMutationAsConflict(
    String id,
    String localSnapshot,
    int serverVersion,
  ) async {
    await (update(mutationQueue)..where((m) => m.id.equals(id))).write(
      MutationQueueCompanion(
        status: const Value('failed'),
        localContentSnapshot: Value(localSnapshot),
        baseVersion: Value(serverVersion),
      ),
    );
  }

  /// Update a mutation's base_version and reset it to pending so it can be retried.
  /// Used after conflict resolution (keep-local or manual-edit flows).
  Future<void> updateMutationBaseVersion(String id, int newBaseVersion) async {
    final mutation = await (select(
      mutationQueue,
    )..where((m) => m.id.equals(id))).getSingleOrNull();

    if (mutation != null) {
      developer.log(
        '[DB:UPDATE_BASE_VERSION] id=$id path=${mutation.path} '
        'baseVersion:${mutation.baseVersion}→$newBaseVersion status:${mutation.status}→pending',
        name: 'AppDatabase',
      );
    }

    await (update(mutationQueue)..where((m) => m.id.equals(id))).write(
      MutationQueueCompanion(
        status: const Value('pending'),
        baseVersion: Value(newBaseVersion),
        conflictFilePath: const Value(null),
      ),
    );
  }

  /// Reset a failed mutation back to pending status.
  Future<void> resetMutation(String id) async {
    final mutation = await (select(
      mutationQueue,
    )..where((m) => m.id.equals(id))).getSingleOrNull();

    if (mutation != null) {
      developer.log(
        '[DB:RESET_MUTATION] id=$id path=${mutation.path} '
        'baseVersion:${mutation.baseVersion} status:${mutation.status}→pending',
        name: 'AppDatabase',
      );
    }

    await (update(mutationQueue)..where((m) => m.id.equals(id))).write(
      const MutationQueueCompanion(status: Value('pending')),
    );
  }

  /// Get count of pending mutations.
  Future<int> getPendingMutationCount() async {
    final count =
        await (selectOnly(mutationQueue)
              ..addColumns([mutationQueue.id.count()])
              ..where(mutationQueue.status.equals('pending')))
            .getSingle();
    return count.read(mutationQueue.id.count()) ?? 0;
  }

  /// Clear all mutations (use with caution).
  Future<void> clearAllMutations() => delete(mutationQueue).go();

  // --- Chat History Operations ---

  /// Insert a chat message (user query + AI response pair).
  Future<int> insertChatMessage({
    required String query,
    required String response,
    String? sources,
    String? attachments,
    required String timestamp,
  }) {
    return into(chatMessages).insert(
      ChatMessagesCompanion.insert(
        query: query,
        response: response,
        sources: Value(sources),
        attachments: Value(attachments),
        timestamp: timestamp,
      ),
    );
  }

  /// Get all chat messages ordered by timestamp (newest last).
  Future<List<ChatMessage>> getAllChatMessages() {
    return (select(chatMessages)
          ..orderBy([(m) => OrderingTerm.asc(m.timestamp)]))
        .get();
  }

  /// Delete a single chat message by ID.
  Future<void> deleteChatMessage(int id) {
    return (delete(chatMessages)..where((m) => m.id.equals(id))).go();
  }

  /// Clear all chat history.
  Future<void> clearChatHistory() => delete(chatMessages).go();
}

LazyDatabase _openConnection() {
  return LazyDatabase(() async {
    final dbDir = await getApplicationDocumentsDirectory();
    final file = File(p.join(dbDir.path, 'jarvis_cache.db'));
    return NativeDatabase.createInBackground(file);
  });
}
