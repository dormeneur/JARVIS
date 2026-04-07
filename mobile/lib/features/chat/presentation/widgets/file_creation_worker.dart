import 'dart:convert';
import 'dart:io';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:path_provider/path_provider.dart';
import 'package:path/path.dart' as p;
import 'package:drift/drift.dart' as drift;

import '../../data/file_manifest_model.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';
import 'package:jarvis_mobile/shared/utils/hash_utils.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

final fileCreationWorkerProvider = Provider<FileCreationWorker>((ref) {
  final db = ref.watch(appDatabaseProvider);
  return FileCreationWorker(db, ref);
});

class FileCreationWorker {
  final AppDatabase _db;
  final Ref _ref;

  FileCreationWorker(this._db, this._ref);

  Future<void> executeCreation(List<FileManifestItem> items) async {
    final batchId = DateTime.now().toUtc().toIso8601String();
    final createdPaths = <String>[];

    final docsDir = await getApplicationDocumentsDirectory();
    final mirrorDir = Directory(p.join(docsDir.path, 'jarvis_mirror'));
    if (!mirrorDir.existsSync()) {
      mirrorDir.createSync(recursive: true);
    }

    for (var item in items) {
      final fileAbsPath = p.join(mirrorDir.path, item.path);
      
      if (item.type == 'directory') {
        final dir = Directory(fileAbsPath);
        if (!dir.existsSync()) {
          dir.createSync(recursive: true);
        }
        createdPaths.add(item.path);

        // Directories normally don't sync directly in JARVIS, they sync transitively through files, 
        // but we record it for the undo log interface locally.
      } else {
        final file = File(fileAbsPath);
        if (!file.parent.existsSync()) {
          file.parent.createSync(recursive: true);
        }
        
        await file.writeAsString(item.content);
        createdPaths.add(item.path);

        final hash = sha256String(item.content);
        final mtime = nowUtcIso8601();

        await _db.upsertEntry(
          FileCacheEntriesCompanion(
            path: drift.Value(item.path),
            name: drift.Value(p.basename(item.path)),
            type: const drift.Value('file'),
            sizeBytes: drift.Value(item.content.length),
            lastModified: drift.Value(mtime),
            contentHash: drift.Value(hash),
            localPath: drift.Value(fileAbsPath),
            lastSynced: drift.Value(mtime),
            serverVersion: const drift.Value(1),
          ),
        );

        await _db.enqueueMutation(
          id: 'create-${DateTime.now().millisecondsSinceEpoch}-${item.path.hashCode}',
          path: item.path,
          operation: 'create',
          timestamp: mtime,
          baseVersion: 1,
        );
      }
    }

    // Save to Undo log via SharedPreferences
    await _logUndoBatch(batchId, createdPaths);

    // Refresh UI
    _ref.invalidate(directoryEntriesProvider);
  }

  Future<void> _logUndoBatch(String batchId, List<String> paths) async {
    final prefs = await SharedPreferences.getInstance();
    final String? existingJson = prefs.getString('jarvis_creation_undo_log');
    
    List<dynamic> logs = [];
    if (existingJson != null) {
      try {
        logs = jsonDecode(existingJson);
      } catch (_) {}
    }

    // Prepend new batch
    logs.insert(0, {
      'batchId': batchId,
      'timestamp': DateTime.now().toIso8601String(),
      'paths': paths,
    });

    // Keep only the last 5 logs for Phase 1 requirements
    if (logs.length > 5) {
      logs = logs.sublist(0, 5);
    }

    await prefs.setString('jarvis_creation_undo_log', jsonEncode(logs));
  }
}
