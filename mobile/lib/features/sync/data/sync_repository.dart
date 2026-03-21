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

    // Determine which paths have local changes (pending or failed mutations).
    final pendingPaths = <String>{};
    final pending = await _db.getPendingMutations();
    final failed = await _db.getFailedMutations();
    for (final m in pending) {
      pendingPaths.add(m.path);
    }
    for (final m in failed) {
      pendingPaths.add(m.path);
    }

    return files.where((f) => f.isFile && f.contentHash != null).map((f) {
      final entry = f.toManifestEntry();
      entry['has_local_changes'] = pendingPaths.contains(f.path);
      return entry;
    }).toList();
  }

  /// Full sync flow: process mutation queue → manifest diff → push → pull.
  Future<SyncResult> performSync() async {
    try {
      int pushed = 0;
      int pulled = 0;
      final conflictPaths = <String>[];
      // Track paths handled in Phase 1 so Phase 3 skips them.
      final processedPaths = <String>{};
      // Track paths that already have a conflict row to prevent duplicates.
      final conflictedPaths = <String>{};

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

            log(
              '[PUSH:PHASE1] Pushing mutation mutationId=${mutation.id} '
              'path=${mutation.path} baseVersion=${mutation.baseVersion}',
              name: 'SyncRepository',
            );

            final pushResult = await _pushFile(
              mutation.path,
              file,
              entry.lastModified,
              mutation.baseVersion,
            );

            log(
              '[PUSH:PHASE1:RESULT] mutationId=${mutation.id} path=${mutation.path} '
              'is_conflict=${pushResult['is_conflict']} result=$pushResult',
              name: 'SyncRepository',
            );

            if (pushResult['is_conflict'] == true) {
              // Read local content BEFORE we do anything else
              final localContent = await file.readAsString();
              final serverVer = pushResult['server_version'] as int? ?? 1;
              log(
                '[PUSH:PHASE1:CONFLICT] mutationId=${mutation.id} '
                'path=${mutation.path} serverVersion=$serverVer',
                name: 'SyncRepository',
              );
              await _db.markMutationAsConflict(
                mutation.id,
                localContent,
                serverVer,
              );
              conflictPaths.add(mutation.path);
              conflictedPaths.add(mutation.path);
            } else {
              // Success - remove from queue and update last_synced + server_version
              log(
                '[PUSH:PHASE1:SUCCESS] mutationId=${mutation.id} path=${mutation.path} '
                'newVersion=${pushResult['version']}',
                name: 'SyncRepository',
              );
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
      log(
        '[SYNC:P2] LOCAL MANIFEST — files=${localManifest.length} '
        'entries=${localManifest.map((e) => "${e['path']}:v${e['serverVersion']}").join(", ")}',
        name: 'SyncRepository',
      );

      final diffResponse = await _postManifest(localManifest);
      log(
        '[SYNC:P2] MANIFEST DIFF RESPONSE raw=$diffResponse',
        name: 'SyncRepository',
      );

      // FIX: Null-safe JSON parsing - server may omit empty arrays
      final toPush = ((diffResponse['to_push'] as List?) ?? [])
          .map((e) => e['path'] as String)
          .toList();
      final toPull = ((diffResponse['to_pull'] as List?) ?? [])
          .map((e) => e['path'] as String)
          .toList();
      // Parse conflicts with server version for each path.
      final conflictEntries = ((diffResponse['conflicts'] as List?) ?? [])
          .cast<Map<String, dynamic>>();
      final conflicts = conflictEntries
          .map((e) => e['path'] as String)
          .toList();
      // Map from conflict path → server version (for correct baseVersion).
      final conflictServerVersions = <String, int>{};
      for (final e in conflictEntries) {
        final v = e['version'];
        if (v != null) conflictServerVersions[e['path'] as String] = v as int;
      }

      log(
        '[SYNC] MANIFEST diff — toPush:${toPush.length} '
        'toPull:${toPull.length} manifestConflicts:${conflicts.length} '
        'paths=$conflicts',
        name: 'SyncRepository',
      );

      // Persist manifest-diff conflicts as synthetic mutation rows.
      // Skip paths already conflicted in Phase 1 to prevent duplicates.
      for (final cp in conflicts) {
        if (conflictedPaths.contains(cp)) {
          log(
            '[SYNC:P2] skip $cp (already conflicted in Phase 1)',
            name: 'SyncRepository',
          );
          continue;
        }

        // Double-check DB for any existing row for this path.
        final currentPending = await _db.getPendingMutations();
        final currentFailed = await _db.getFailedMutations();
        final hasRow =
            currentPending.any((m) => m.path == cp) ||
            currentFailed.any((m) => m.path == cp);

        if (hasRow) {
          // Row already exists — count it (it's a real conflict from Phase 1)
          conflictPaths.add(cp);
          log(
            '[SYNC:P2] skip $cp (existing mutation row)',
            name: 'SyncRepository',
          );
          continue;
        }

        // Read local content for snapshot
        String localSnapshot = '';
        final entry = await _explorerRepo.getEntry(cp);
        if (entry?.localPath != null) {
          final f = File(entry!.localPath!);
          if (f.existsSync()) localSnapshot = await f.readAsString();
        }

        final baseVer = conflictServerVersions[cp] ?? entry?.serverVersion ?? 1;
        final syntheticId =
            'conflict-${DateTime.now().millisecondsSinceEpoch}'
            '-${cp.hashCode.abs()}';

        await _db.enqueueMutation(
          id: syntheticId,
          path: cp,
          operation: 'update',
          timestamp: DateTime.now().toUtc().toIso8601String(),
          baseVersion: baseVer,
        );
        // Mark as conflict with local snapshot
        await _db.markMutationAsConflict(syntheticId, localSnapshot, baseVer);
        conflictedPaths.add(cp);
        conflictPaths.add(cp); // Only count after row is persisted

        log(
          '[SYNC:P2] conflict persisted path=$cp id=$syntheticId',
          name: 'SyncRepository',
        );
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
        // Guard: never auto-push a path the server flagged as conflicted.
        if (conflictedPaths.contains(path)) {
          log(
            '[SYNC:P3] skipping path=$path (flagged as conflict)',
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
        '[PUSH:PRE] path=$path baseVersion=$baseVersion '
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

      log(
        '[PUSH:PAYLOAD] path=$path metadata=$metadata',
        name: 'SyncRepository',
      );

      final formData = FormData.fromMap({
        'metadata': metadata,
        'file': MultipartFile.fromBytes(bytes, filename: p.basename(path)),
      });

      final response = await _apiClient.dio.post('/sync/push', data: formData);
      final data = response.data as Map<String, dynamic>;

      log('[PUSH:RESPONSE] raw=$data', name: 'SyncRepository');

      // Parse conflict response
      final conflicts = (data['conflicts'] as List?) ?? [];
      if (conflicts.isNotEmpty) {
        final conflictEntry = conflicts.first as Map<String, dynamic>;
        final serverVersion = conflictEntry['version'] as int? ?? 1;
        log(
          '[PUSH:CONFLICT] path=$path serverVersion=$serverVersion',
          name: 'SyncRepository',
        );
        return {
          'is_conflict': true,
          'path': path,
          'server_version': serverVersion,
        };
      }

      // Extract new version from successful push
      final accepted = (data['accepted'] as List?) ?? [];
      if (accepted.isNotEmpty) {
        final pushEntry = accepted.first as Map<String, dynamic>;
        final newVersion = pushEntry['version'] ?? baseVersion + 1;
        log(
          '[PUSH:SUCCESS] path=$path baseVersion=$baseVersion '
          'newVersion=$newVersion',
          name: 'SyncRepository',
        );
        return {'is_conflict': false, 'path': path, 'version': newVersion};
      }

      log(
        '[PUSH:SUCCESS:EMPTY] path=$path baseVersion=$baseVersion '
        'version:${baseVersion + 1} (empty pushed list)',
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

  /// Fetch file content from the server without saving to disk.
  /// Used by the conflict detail screen to display the remote version.
  Future<String?> fetchRemoteContent(String path) async {
    try {
      final response = await _apiClient.dio.post(
        '/sync/pull',
        data: {'path': path},
        options: Options(responseType: ResponseType.bytes),
      );
      final bytes = response.data as List<int>;
      return utf8.decode(bytes);
    } on DioException catch (e) {
      log(
        '[FETCH_REMOTE] Failed to fetch remote content for path=$path: $e',
        name: 'SyncRepository',
      );
      return null;
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
  // Testing Utilities
  // ---------------------------------------------------------------------------

  /// Nuclear reset: wipe server vault, local mirror, SQLite cache, and
  /// mutation queue. Used for testing the sync pipeline from a clean state.
  Future<void> resetEverything() async {
    try {
      // 1. Wipe server vault + version tracker
      await _apiClient.dio.post('/files/reset');
      log('[RESET] Server vault wiped', name: 'SyncRepository');

      // 2. Delete local mirror directory
      final mirror = await _mirrorDir();
      if (mirror.existsSync()) {
        mirror.deleteSync(recursive: true);
        log('[RESET] Local mirror deleted', name: 'SyncRepository');
      }

      // 3. Clear SQLite file cache
      await _db.deleteAllEntries();
      log('[RESET] SQLite file cache cleared', name: 'SyncRepository');

      // 4. Clear mutation queue
      await _db.clearAllMutations();
      log('[RESET] Mutation queue cleared', name: 'SyncRepository');
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// Create a file on the server and pull it to the local mirror.
  /// Returns the server-assigned path.
  Future<String> createFileOnServer(String path, String content) async {
    try {
      final response = await _apiClient.dio.post(
        '/files/$path',
        data: {'content': content, 'type': 'file'},
      );
      final data = response.data as Map<String, dynamic>;
      final serverPath = data['path'] as String;

      log(
        '[CREATE] File created on server: $serverPath',
        name: 'SyncRepository',
      );

      // Pull it locally so it appears immediately in the explorer
      await _pullFile(serverPath);

      return serverPath;
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// Create a directory on the server with a .gitkeep placeholder so
  /// it appears on mobile (which infers directories from file paths).
  Future<String> createFolderOnServer(String path) async {
    try {
      // 1. Create the directory on the server
      final response = await _apiClient.dio.post(
        '/files/$path',
        data: {'content': '', 'type': 'directory'},
      );
      final data = response.data as Map<String, dynamic>;
      final serverPath = data['path'] as String;

      // 2. Create a .gitkeep inside so the folder is visible on mobile
      final keepPath = '$serverPath/.gitkeep';
      await _apiClient.dio.post(
        '/files/$keepPath',
        data: {'content': '', 'type': 'file'},
      );

      // 3. Pull the .gitkeep locally so the directory shows in explorer
      await _pullFile(keepPath);

      log(
        '[CREATE] Folder created on server: $serverPath (with .gitkeep)',
        name: 'SyncRepository',
      );

      return serverPath;
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  // ---------------------------------------------------------------------------
  // Conflict Resolution
  // ---------------------------------------------------------------------------

  /// Unified conflict resolution.
  /// Writes [finalContent] to the local mirror, updates SQLite, and
  /// resets the mutation to 'pending' so the next sync pushes it.
  Future<void> resolveConflict(String mutationId, String finalContent) async {
    final mutation = await _db.getMutationById(mutationId);
    if (mutation == null) return;

    // 1. Get server version — use the mutation's stored baseVersion (which
    //    came from the manifest conflict response) as a reliable source.
    //    Fall back to fetching from server if needed.
    int serverVersion = mutation.baseVersion;
    final fetchedVersion = await _fetchServerVersion(mutation.path);
    if (fetchedVersion > serverVersion) {
      serverVersion = fetchedVersion;
    }

    log(
      '[CONFLICT:RESOLVE] id=$mutationId path=${mutation.path} '
      'mutationBaseVersion=${mutation.baseVersion} fetchedVersion=$fetchedVersion '
      'usingVersion=$serverVersion',
      name: 'SyncRepository',
    );

    // 2. Write final content to local mirror
    final mirror = await _mirrorDir();
    final localFile = File(
      p.join(
        mirror.path,
        mutation.path.replaceAll('/', Platform.pathSeparator),
      ),
    );
    localFile.parent.createSync(recursive: true);
    await localFile.writeAsString(finalContent);

    // 3. Update SQLite cache
    final bytes = utf8.encode(finalContent);
    final hash = sha256Hex(bytes);
    final now = nowUtcIso8601();

    await _db.upsertEntry(
      FileCacheEntriesCompanion(
        path: drift.Value(mutation.path),
        name: drift.Value(p.basename(mutation.path)),
        type: const drift.Value('file'),
        sizeBytes: drift.Value(bytes.length),
        lastModified: drift.Value(now),
        contentHash: drift.Value(hash),
        localPath: drift.Value(localFile.path),
        serverVersion: drift.Value(serverVersion),
      ),
    );

    // 4. Reset mutation to pending with correct base version
    await _db.updateMutationBaseVersion(mutationId, serverVersion);

    log(
      '[CONFLICT:RESOLVED] id=$mutationId path=${mutation.path} '
      'baseVersion=$serverVersion',
      name: 'SyncRepository',
    );
  }

  /// Fetch the current server version for a file path via pull headers.\n  /// Returns 0 on failure so the caller can detect the failure and use a\n  /// better fallback (e.g. mutation.baseVersion) instead of a wrong version.
  Future<int> _fetchServerVersion(String path) async {
    try {
      final response = await _apiClient.dio.post(
        '/sync/pull',
        data: {'path': path},
        options: Options(responseType: ResponseType.bytes),
      );
      final versionHeader = response.headers.value('X-File-Version');
      if (versionHeader != null) {
        final parsed = int.tryParse(versionHeader);
        if (parsed != null && parsed > 0) return parsed;
      }
      log(
        '[FETCH_VERSION] Missing or invalid X-File-Version header for path=$path',
        name: 'SyncRepository',
      );
      return 0;
    } catch (e) {
      log('[FETCH_VERSION] Failed for path=$path: $e', name: 'SyncRepository');
      return 0;
    }
  }
}
