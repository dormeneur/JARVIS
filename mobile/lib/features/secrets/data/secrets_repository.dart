import 'dart:convert';
import 'dart:io';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:drift/drift.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/network/api_exceptions.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/secrets/domain/crypto_service.dart';
import 'package:path/path.dart' as p;
import 'package:path_provider/path_provider.dart';
import 'package:jarvis_mobile/shared/utils/hash_utils.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';

class SecretsRepository {
  final AppDatabase _db;
  final CryptoService _cryptoService;
  final ApiClient _apiClient;

  SecretsRepository({
    required AppDatabase db,
    required CryptoService cryptoService,
    required ApiClient apiClient,
  }) : _db = db,
       _cryptoService = cryptoService,
       _apiClient = apiClient;

  Future<Directory> _mirrorDir() async {
    final docsDir = await getApplicationDocumentsDirectory();
    return Directory(p.join(docsDir.path, 'jarvis_mirror'));
  }

  /// Saves a secret client-side.
  /// 
  /// 1. Encrypts the value using the provided [derivedKey].
  /// 2. Saves to SQLite [SecretEntries] for local UI.
  /// 3. Saves encrypted blob to physical file in /Secrets/ for sync.
  /// 4. Enqueues a mutation for the file.
  Future<void> saveSecret({
    required String id,
    required String label,
    required String value,
    required Uint8List derivedKey,
    required Uint8List salt,
    required Uint8List iv,
  }) async {
    final encryptedBlob = _cryptoService.encrypt(derivedKey, value, iv);
    final now = nowUtcIso8601();

    // 1. Update SecretEntries in SQLite (for local display)
    await _db.upsertSecret(
      SecretEntriesCompanion(
        id: Value(id),
        label: Value(label),
        encryptedBlob: Value(base64Encode(encryptedBlob)),
        iv: Value(base64Encode(iv)),
        salt: Value(base64Encode(salt)),
        createdAt: Value(now),
        updatedAt: Value(now),
      ),
    );

    // 2. Prepare physical file for sync
    final mirror = await _mirrorDir();
    final secretsFolder = Directory(p.join(mirror.path, 'Secrets'));
    if (!secretsFolder.existsSync()) {
      secretsFolder.createSync(recursive: true);
    }

    // Filename is {id}.jvs
    final fileName = '$id.jvs';
    final filePath = 'Secrets/$fileName';
    final absolutePath = p.join(secretsFolder.path, fileName);
    final file = File(absolutePath);

    // The blob stored in file is: JVS\x01 + salt + iv + ciphertext
    // Actually, the requirement said "opaque binary blobs only".
    // I'll stick to a simple format for the file: 4 bytes magic, 16 bytes salt, 12 bytes IV, then ciphertext.
    // This allows syncing to other devices where they can parse it.
    final List<int> fileContent = [];
    fileContent.addAll(utf8.encode('JVSP')); // JARVIS Secret Pin-protected
    fileContent.addAll(salt);
    fileContent.addAll(iv);
    fileContent.addAll(encryptedBlob);
    
    final fileBytes = Uint8List.fromList(fileContent);
    await file.writeAsBytes(fileBytes);

    // 3. Update FileCacheEntries so SyncRepository picks it up
    final hash = sha256Hex(fileBytes);
    await _db.upsertEntry(
      FileCacheEntriesCompanion(
        path: Value(filePath),
        name: Value(fileName),
        type: const Value('file'),
        sizeBytes: Value(fileBytes.length),
        lastModified: Value(now),
        contentHash: Value(hash),
        localPath: Value(absolutePath),
        lastSynced: const Value(null),
        serverVersion: const Value(1),
      ),
    );

    // 4. Enqueue mutation
    await _db.enqueueMutation(
      id: 'sec-${DateTime.now().millisecondsSinceEpoch}-$id',
      path: filePath,
      operation: 'update',
      timestamp: now,
      baseVersion: 1,
    );
  }

  Future<List<SecretEntry>> getAllSecrets() => _db.getAllSecrets();

  Future<void> deleteSecret(String id) async {
    final filePath = 'Secrets/$id.jvs';

    // 1. Remove from SecretEntries (local SQLite)
    await _db.deleteSecret(id);

    // 2. Remove from FileCacheEntries and delete physical file
    final entry = await _db.getEntry(filePath);
    final baseVersion = entry?.serverVersion ?? 1;

    if (entry?.localPath != null) {
      final file = File(entry!.localPath!);
      if (file.existsSync()) {
        file.deleteSync();
      }
    }
    await _db.deleteEntry(filePath);

    // Cancel any pending mutations for this path to prevent stale re-upload.
    await _db.removeMutationsForPath(filePath);

    // 3. Try to delete from server immediately (best-effort, server-first).
    // If the server is reachable, delete synchronously — this prevents the
    // stale conflict loop that occurs when a delete mutation sits in the
    // queue and the manifest diff sees the file as conflicted.
    // If the server is unreachable, fall back to queuing a mutation.
    bool serverDeleted = false;
    try {
      final response = await _apiClient.dio.delete('/files/$filePath');
      debugPrint('[SecretsRepo] Server DELETE $filePath → ${response.statusCode}');
      serverDeleted = true;
    } on DioException catch (e) {
      debugPrint('[SecretsRepo] Server DELETE failed: ${e.response?.statusCode} ${e.message}');
      final mapped = mapDioError(e);
      if (mapped.statusCode == 404) {
        serverDeleted = true;
      }
    } catch (e) {
      debugPrint('[SecretsRepo] Server DELETE unexpected error: $e');
    }
    debugPrint('[SecretsRepo] serverDeleted=$serverDeleted');

    if (!serverDeleted) {
      // 4. Fallback: enqueue delete mutation for offline deletion.
      await _db.enqueueMutation(
        id: 'del-sec-${DateTime.now().millisecondsSinceEpoch}-$id',
        path: filePath,
        operation: 'delete',
        timestamp: nowUtcIso8601(),
        baseVersion: baseVersion,
      );
    }
  }
}
