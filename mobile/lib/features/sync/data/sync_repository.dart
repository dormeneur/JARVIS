import 'dart:convert';
import 'dart:developer';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:jarvis_mobile/core/errors/app_error.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/network/api_exceptions.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/shared/models/sync_result.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';
import 'package:jarvis_mobile/shared/utils/hash_utils.dart';
import 'package:path_provider/path_provider.dart';
import 'package:drift/drift.dart' as drift;
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:path/path.dart' as p;

/// Orchestrates the sync flow: manifest → push/pull → update SQLite.
class SyncRepository {
  final ApiClient _apiClient;
  final ExplorerRepository _explorerRepo;
  final AppDatabase _db;

  SyncRepository({
    required ApiClient apiClient,
    required ExplorerRepository explorerRepo,
    required AppDatabase db,
  }) : _apiClient = apiClient,
       _explorerRepo = explorerRepo,
       _db = db;

  /// Get the local mirror directory.
  Future<Directory> _mirrorDir() async {
    final docsDir = await getApplicationDocumentsDirectory();
    final mirror = Directory(p.join(docsDir.path, 'jarvis_mirror'));
    if (!mirror.existsSync()) {
      mirror.createSync(recursive: true);
    }
    return mirror;
  }

  /// Build a manifest from local SQLite file entries.
  Future<List<Map<String, dynamic>>> buildLocalManifest() async {
    final files = await _explorerRepo.getAllFiles();
    return files
        .where((f) => f.isFile && f.contentHash != null)
        .map((f) => f.toManifestEntry())
        .toList();
  }

  /// Full sync flow: process mutation queue → manifest diff → push → pull.
  Future<SyncResult> performSync() async {
    try {
      int pushed = 0;
      int pulled = 0;
      final conflictPaths = <String>[];
      // FIX Bug C: track paths fully handled in Phase 1 so Phase 3 skips them.
      final processedPaths = <String>{};

      // PHASE 1: Process mutation queue first
      final pendingMutations = await _db.getPendingMutations();
      final failedMutations = await _db.getFailedMutations();
      log(
        '[SYNC] START — pending:${pendingMutations.length} '
        'failed:${failedMutations.length}',
        name: 'SyncRepository',
      );

      for (final mutation in pendingMutations) {
        try {
          log(
            '[SYNC:P1] mutation id=${mutation.id} path=${mutation.path} '
            'op=${mutation.operation} baseVersion=${mutation.baseVersion}',
            name: 'SyncRepository',
          );
          if (mutation.operation == 'delete') {
            // Delete file on server
            await _deleteFile(mutation.path);
            // Remove mutation from queue on success
            await _db.removeMutation(mutation.id);
            pushed++; // Count as a push operation
          } else if (mutation.operation == 'create' ||
              mutation.operation == 'update') {
            // Push file to server
            final entry = await _explorerRepo.getEntry(mutation.path);
            if (entry == null || entry.localPath == null) {
              // File no longer exists locally, remove mutation
              await _db.removeMutation(mutation.id);
              continue;
            }

            final file = File(entry.localPath!);
            if (!file.existsSync()) {
              // File deleted locally, remove mutation
              await _db.removeMutation(mutation.id);
              continue;
            }

            final pushResult = await _pushFile(
              mutation.path,
              file,
              entry.lastModified,
              mutation.baseVersion,
            );

            if (pushResult['is_conflict'] == true) {
              // Mark mutation as failed with conflict file path
              final conflictPath =
                  pushResult['conflict_file_path'] as String? ?? '';
              if (conflictPath.isNotEmpty) {
                await _db.markMutationConflict(mutation.id, conflictPath);
              } else {
                await _db.markMutationFailed(mutation.id);
              }
              conflictPaths.add(pushResult['path'] as String);
            } else {
              // Success - remove from queue and update last_synced + server_version
              await _db.removeMutation(mutation.id);
              final newVersion = pushResult['version'] as int;
              await _db.upsertEntry(
                FileCacheEntriesCompanion(
                  path: drift.Value(mutation.path),
                  name: drift.Value(entry.name),
                  type: drift.Value('file'),
                  sizeBytes: drift.Value(entry.sizeBytes),
                  lastModified: drift.Value(entry.lastModified),
                  contentHash: drift.Value(entry.contentHash),
                  localPath: drift.Value(entry.localPath),
                  lastSynced: drift.Value(nowUtcIso8601()),
                  serverVersion: drift.Value(newVersion),
                ),
              );
              pushed++;
              processedPaths.add(mutation.path);
            }
          }
        } catch (e) {
          // Mark mutation as failed on error
          await _db.markMutationFailed(mutation.id);
          // Continue with other mutations
        }
      }

      // PHASE 2: Build local manifest and send to server
      final localManifest = await buildLocalManifest();
      final diffResponse = await _postManifest(localManifest);

      // FIX: Null-safe JSON parsing - server may omit empty arrays
      final toPush = ((diffResponse['to_push'] as List?) ?? [])
          .map((e) => e['path'] as String)
          .toList();
      final toPull = ((diffResponse['to_pull'] as List?) ?? [])
          .map((e) => e['path'] as String)
          .toList();
      final conflicts = ((diffResponse['conflicts'] as List?) ?? [])
          .map((e) => e['path'] as String)
          .toList();

      log(
        '[SYNC] MANIFEST diff — toPush:${toPush.length} '
        'toPull:${toPull.length} manifestConflicts:${conflicts.length} '
        'paths=$conflicts',
        name: 'SyncRepository',
      );

      // FIX Bug B: persist each manifest-diff conflict as a synthetic failed
      // mutation row so the UI can display it and resolution flows are reachable.
      // Only create a row when no existing row (pending or failed) covers the path.
      for (final conflictPath in conflicts) {
        conflictPaths.add(conflictPath);

        // Check for any existing mutation row for this path (pending or failed).
        // IMPORTANT: Query DB again here (not using cached lists from start of sync)
        // because Phase 1 may have modified the mutation queue.
        final currentPending = await _db.getPendingMutations();
        final currentFailed = await _db.getFailedMutations();
        
        final pendingForPath = currentPending.any((m) => m.path == conflictPath);
        final failedForPath = currentFailed.any((m) => m.path == conflictPath);

        if (!pendingForPath && !failedForPath) {
          // No existing row — create a conflict placeholder.
          final entry = await _explorerRepo.getEntry(conflictPath);
          final baseVer = entry?.serverVersion ?? 1;
          final syntheticId =
              'manifest-conflict-${DateTime.now().millisecondsSinceEpoch}'
              '-${conflictPath.hashCode.abs()}';

          await _db.enqueueMutation(
            id: syntheticId,
            path: conflictPath,
            operation: 'update',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: baseVer,
          );
          // Immediately mark failed (conflict placeholder — never auto-pushed).
          await _db.markMutationFailed(syntheticId);

          log(
            '[SYNC:P2] manifest conflict persisted — path=$conflictPath '
            'id=$syntheticId baseVersion=$baseVer',
            name: 'SyncRepository',
          );
        } else {
          log(
            '[SYNC:P2] manifest conflict already has mutation row — '
            'path=$conflictPath (skipCreate)',
            name: 'SyncRepository',
          );
        }
      }

      // PHASE 3: Push remaining files (from manifest diff)
      for (final path in toPush) {
        // FIX Bug C: skip paths already successfully pushed in Phase 1.
        if (processedPaths.contains(path)) {
          log(
            '[SYNC:P3] skipping path=$path (already handled in Phase 1)',
            name: 'SyncRepository',
          );
          continue;
        }
        log('[SYNC:P3] attempting push path=$path', name: 'SyncRepository');
        final entry = await _explorerRepo.getEntry(path);
        if (entry == null || entry.localPath == null) continue;

        final file = File(entry.localPath!);
        if (!file.existsSync()) continue;

        final pushResult = await _pushFile(
          path,
          file,
          entry.lastModified,
          entry.serverVersion,
        );

        if (pushResult['is_conflict'] == true) {
          conflictPaths.add(pushResult['path'] as String);
        } else {
          pushed++;
          final newVersion = pushResult['version'] as int;
          // Update last_synced and server_version
          await _db.upsertEntry(
            FileCacheEntriesCompanion(
              path: drift.Value(path),
              name: drift.Value(entry.name),
              type: drift.Value('file'),
              sizeBytes: drift.Value(entry.sizeBytes),
              lastModified: drift.Value(entry.lastModified),
              contentHash: drift.Value(entry.contentHash),
              localPath: drift.Value(entry.localPath),
              lastSynced: drift.Value(nowUtcIso8601()),
              serverVersion: drift.Value(newVersion),
            ),
          );
        }
      }

      // PHASE 4: Pull files from server
      for (final path in toPull) {
        try {
          await _pullFile(path);
          pulled++;
        } on NetworkError catch (e) {
          if (e.statusCode == 404) {
            // Stale file — remove local entry and file
            final entry = await _explorerRepo.getEntry(path);
            if (entry?.localPath != null) {
              final file = File(entry!.localPath!);
              if (file.existsSync()) file.deleteSync();
            }
            await _explorerRepo.removeEntry(path);
          } else {
            rethrow;
          }
        }
      }

      log(
        '[SYNC] DONE — pushed:$pushed pulled:$pulled '
        'conflicts:${conflictPaths.length} conflictPaths:$conflictPaths',
        name: 'SyncRepository',
      );

      return SyncResult(
        pushed: pushed,
        pulled: pulled,
        conflicts: conflictPaths.length,
        conflictPaths: conflictPaths,
      );
    } on DioException catch (e) {
      throw SyncError('Sync failed: ${mapDioError(e).message}', cause: e);
    }
  }

  /// POST /sync/manifest
  Future<Map<String, dynamic>> _postManifest(
    List<Map<String, dynamic>> manifest,
  ) async {
    try {
      final response = await _apiClient.dio.post(
        '/sync/manifest',
        data: {'manifest': manifest},
      );
      return response.data as Map<String, dynamic>;
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// POST /sync/push (multipart)
  Future<Map<String, dynamic>> _pushFile(
    String path,
    File file,
    String lastModified,
    int baseVersion,
  ) async {
    try {
      // Diagnostic: capture SQLite serverVersion before push
      final cacheEntry = await _db.getEntry(path);
      final sqliteServerVersion = cacheEntry?.serverVersion ?? 'null';
      log(
        '[PUSH] path=$path baseVersion=$baseVersion '
        'sqliteServerVersion=$sqliteServerVersion',
        name: 'SyncRepository',
      );

      final bytes = await file.readAsBytes();
      final hash = sha256Hex(bytes);

      final metadata = jsonEncode({
        'path': path,
        'content_hash': hash,
        'last_modified': lastModified,
        'base_version': baseVersion,
      });

      final formData = FormData.fromMap({
        'metadata': metadata,
        'file': MultipartFile.fromBytes(bytes, filename: p.basename(path)),
      });

      final response = await _apiClient.dio.post('/sync/push', data: formData);
      final data = response.data as Map<String, dynamic>;

      // FIX: Null-safe JSON parsing
      final conflicts = (data['conflicts'] as List?) ?? [];
      if (conflicts.isNotEmpty) {
        final conflictEntry = conflicts.first;
        final conflictFilePath = conflictEntry['path'] as String? ?? '';
        log(
          '[PUSH] response — isConflict:true conflictPath:$conflictFilePath '
          'version:null raw:$data',
          name: 'SyncRepository',
        );
        return {
          'is_conflict': true,
          'path': path,
          'conflict_file_path': conflictFilePath,
        };
      }

      // Extract new version from successful push
      final pushed = (data['pushed'] as List?) ?? [];
      if (pushed.isNotEmpty) {
        final pushEntry = pushed.first as Map<String, dynamic>;
        final newVersion = pushEntry['version'] ?? baseVersion + 1;
        log(
          '[PUSH] response — isConflict:false conflictPath:null '
          'version:$newVersion raw:$data',
          name: 'SyncRepository',
        );
        return {'is_conflict': false, 'path': path, 'version': newVersion};
      }

      log(
        '[PUSH] response — isConflict:false conflictPath:null '
        'version:${baseVersion + 1} raw:$data (empty pushed list)',
        name: 'SyncRepository',
      );
      return {'is_conflict': false, 'path': path, 'version': baseVersion + 1};
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// POST /sync/pull — download file and save locally.
  Future<void> _pullFile(String path) async {
    try {
      // Diagnostic: capture previous SQLite serverVersion before pull
      final prevEntry = await _db.getEntry(path);
      final prevVersion = prevEntry?.serverVersion ?? 'null';
      log(
        '[PULL] path=$path prevSqliteVersion=$prevVersion',
        name: 'SyncRepository',
      );

      final response = await _apiClient.dio.post(
        '/sync/pull',
        data: {'path': path},
        options: Options(responseType: ResponseType.bytes),
      );

      final bytes = response.data as List<int>;

      // Extract version from response headers
      final versionHeader = response.headers.value('X-File-Version');
      final serverVersion = versionHeader != null
          ? int.tryParse(versionHeader) ?? 1
          : 1;

      final mirror = await _mirrorDir();
      final localFile = File(
        p.join(mirror.path, path.replaceAll('/', Platform.pathSeparator)),
      );

      // Create parent directories
      final parent = localFile.parent;
      if (!parent.existsSync()) {
        parent.createSync(recursive: true);
      }

      await localFile.writeAsBytes(bytes);

      // Compute hash and update SQLite
      final hash = sha256Hex(bytes);
      final name = p.basename(path);
      final now = nowUtcIso8601();

      // Use file mtime as last_modified
      final stat = localFile.statSync();
      final mtime = toUtcIso8601(stat.modified);

      await _db.upsertEntry(
        FileCacheEntriesCompanion(
          path: drift.Value(path),
          name: drift.Value(name),
          type: const drift.Value('file'),
          sizeBytes: drift.Value(bytes.length),
          lastModified: drift.Value(mtime),
          contentHash: drift.Value(hash),
          localPath: drift.Value(localFile.path),
          lastSynced: drift.Value(now),
          serverVersion: drift.Value(serverVersion),
        ),
      );

      log(
        '[PULL] path=$path headerRaw=$versionHeader '
        'prevVersion=$prevVersion newVersion=$serverVersion',
        name: 'SyncRepository',
      );
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// DELETE /files/{path} — delete file on server.
  Future<void> _deleteFile(String path) async {
    try {
      await _apiClient.dio.delete('/files/$path');
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  // ---------------------------------------------------------------------------
  // Conflict Resolution
  // ---------------------------------------------------------------------------

  /// Keep-local resolution: re-queue the failed mutation with the latest
  /// server version so the next sync will push the local content.
  /// Does NOT push immediately — sync remains centralized in [performSync].
  Future<void> resolveKeepLocal(String mutationId) async {
    final mutation = await _db.getMutationById(mutationId);
    if (mutation == null) return;

    // Fetch the current server version for this file from the local cache
    final cacheEntry = await _db.getEntry(mutation.path);
    final latestServerVersion =
        cacheEntry?.serverVersion ?? mutation.baseVersion;

    await _db.updateMutationBaseVersion(mutationId, latestServerVersion);
  }

  /// Accept-remote resolution: fetch the conflict file's content from the
  /// server, overwrite the local mirror at the original path, update SQLite,
  /// then remove the failed mutation.
  /// Does NOT delete the conflict file from the server.
  ///
  /// For Phase 1 conflicts: pulls from conflictFilePath
  /// For Phase 2 conflicts (no conflictFilePath): pulls from original path
  Future<void> resolveAcceptRemote(String mutationId) async {
    final mutation = await _db.getMutationById(mutationId);
    if (mutation == null) return;

    final conflictFilePath = mutation.conflictFilePath;
    
    // Determine which path to pull from
    final pathToPull = (conflictFilePath != null && conflictFilePath.isNotEmpty)
        ? conflictFilePath  // Phase 1: pull from conflict file
        : mutation.path;     // Phase 2: pull from original path

    // Pull the content from the server
    final response = await _apiClient.dio.post(
      '/sync/pull',
      data: {'path': pathToPull},
      options: Options(responseType: ResponseType.bytes),
    );

    final bytes = response.data as List<int>;
    final versionHeader = response.headers.value('X-File-Version');
    final serverVersion = versionHeader != null
        ? int.tryParse(versionHeader) ?? 1
        : 1;

    // Write content to the local mirror at the *original* path (not conflict path)
    final mirror = await _mirrorDir();
    final localFile = File(
      p.join(
        mirror.path,
        mutation.path.replaceAll('/', Platform.pathSeparator),
      ),
    );
    final parent = localFile.parent;
    if (!parent.existsSync()) parent.createSync(recursive: true);
    await localFile.writeAsBytes(bytes);

    // Update SQLite: hash, serverVersion, localPath for the *original* path
    final hash = sha256Hex(bytes);
    final name = p.basename(mutation.path);
    final now = nowUtcIso8601();
    final stat = localFile.statSync();
    final mtime = toUtcIso8601(stat.modified);

    await _db.upsertEntry(
      FileCacheEntriesCompanion(
        path: drift.Value(mutation.path),
        name: drift.Value(name),
        type: const drift.Value('file'),
        sizeBytes: drift.Value(bytes.length),
        lastModified: drift.Value(mtime),
        contentHash: drift.Value(hash),
        localPath: drift.Value(localFile.path),
        lastSynced: drift.Value(now),
        serverVersion: drift.Value(serverVersion),
      ),
    );

    // Remove the failed mutation — no push needed, remote is now canonical
    await _db.removeMutation(mutationId);
  }

  /// Manual-edit resolution: the user has provided merged content.
  /// Overwrites the local file + SQLite, then re-queues the mutation with
  /// the latest server version via [resolveKeepLocal].
  Future<void> resolveManualEdit(
    String mutationId,
    String mergedContent,
  ) async {
    final mutation = await _db.getMutationById(mutationId);
    if (mutation == null) return;

    final cacheEntry = await _db.getEntry(mutation.path);
    if (cacheEntry == null || cacheEntry.localPath == null) return;

    // Write merged content to local mirror
    final localFile = File(cacheEntry.localPath!);
    await localFile.writeAsString(mergedContent);

    // Update SQLite with new hash and size
    final bytes = localFile.readAsBytesSync();
    final hash = sha256Hex(bytes);
    final mtime = nowUtcIso8601();

    await _db.upsertEntry(
      FileCacheEntriesCompanion(
        path: drift.Value(mutation.path),
        name: drift.Value(cacheEntry.name),
        type: const drift.Value('file'),
        sizeBytes: drift.Value(bytes.length),
        lastModified: drift.Value(mtime),
        contentHash: drift.Value(hash),
        localPath: drift.Value(cacheEntry.localPath),
        lastSynced: drift.Value(cacheEntry.lastSynced),
        serverVersion: drift.Value(cacheEntry.serverVersion),
      ),
    );

    // Re-queue with fresh base_version — no immediate push
    await resolveKeepLocal(mutationId);
  }
}
