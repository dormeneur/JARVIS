import 'dart:convert';
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

  /// Full sync flow: manifest diff → push → pull.
  Future<SyncResult> performSync() async {
    try {
      // 1. Build local manifest and send to server
      final localManifest = await buildLocalManifest();
      final diffResponse = await _postManifest(localManifest);

      final toPush = (diffResponse['to_push'] as List)
          .map((e) => e['path'] as String)
          .toList();
      final toPull = (diffResponse['to_pull'] as List)
          .map((e) => e['path'] as String)
          .toList();
      final conflicts = (diffResponse['conflicts'] as List)
          .map((e) => e['path'] as String)
          .toList();

      int pushed = 0;
      int pulled = 0;
      final conflictPaths = <String>[...conflicts];

      // 2. Push local files to server
      for (final path in toPush) {
        final entry = await _explorerRepo.getEntry(path);
        if (entry == null || entry.localPath == null) continue;

        final file = File(entry.localPath!);
        if (!file.existsSync()) continue;

        final pushResult = await _pushFile(path, file, entry.lastModified);
        if (pushResult['is_conflict'] == true) {
          conflictPaths.add(pushResult['path'] as String);
        } else {
          pushed++;
          // Update last_synced
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
            ),
          );
        }
      }

      // 3. Pull files from server
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
  ) async {
    try {
      final bytes = await file.readAsBytes();
      final hash = sha256Hex(bytes);

      final metadata = jsonEncode({
        'path': path,
        'content_hash': hash,
        'last_modified': lastModified,
      });

      final formData = FormData.fromMap({
        'metadata': metadata,
        'file': MultipartFile.fromBytes(bytes, filename: p.basename(path)),
      });

      final response = await _apiClient.dio.post('/sync/push', data: formData);
      final data = response.data as Map<String, dynamic>;

      if ((data['conflicts'] as List).isNotEmpty) {
        final conflictEntry = (data['conflicts'] as List).first;
        return {'is_conflict': true, 'path': conflictEntry['path']};
      }
      return {'is_conflict': false, 'path': path};
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// POST /sync/pull — download file and save locally.
  Future<void> _pullFile(String path) async {
    try {
      final response = await _apiClient.dio.post(
        '/sync/pull',
        data: {'path': path},
        options: Options(responseType: ResponseType.bytes),
      );

      final bytes = response.data as List<int>;
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
        ),
      );
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }
}
