import 'dart:io';
import 'dart:typed_data';
import 'package:drift/drift.dart' hide isNull, isNotNull;
import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/secrets/data/secrets_repository.dart';
import 'package:jarvis_mobile/features/secrets/domain/crypto_service.dart';
import 'package:path_provider_platform_interface/path_provider_platform_interface.dart';
import 'package:plugin_platform_interface/plugin_platform_interface.dart';

class _FakePathProvider extends PathProviderPlatform with MockPlatformInterfaceMixin {
  final String tempPath;
  _FakePathProvider(this.tempPath);

  @override
  Future<String?> getApplicationDocumentsPath() async => tempPath;
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late AppDatabase db;
  late SecretsRepository repository;
  late CryptoService cryptoService;
  late Directory tempDir;

  setUp(() async {
    tempDir = await Directory.systemTemp.createTemp('secrets_sync_test_');
    PathProviderPlatform.instance = _FakePathProvider(tempDir.path);
    
    db = AppDatabase.forTesting(NativeDatabase.memory());
    cryptoService = CryptoService();
    repository = SecretsRepository(db: db, cryptoService: cryptoService);
  });

  tearDown(() async {
    await db.close();
    await tempDir.delete(recursive: true);
  });

  test('Saving a secret creates DB entry, physical file, and mutation', () async {
    final derivedKey = Uint8List(32)..fillRange(0, 32, 1);
    final salt = Uint8List(16)..fillRange(0, 16, 2);
    final iv = Uint8List(12)..fillRange(0, 12, 3);
    
    const id = 'test-secret-id';
    const label = 'Gmail';
    const value = 'password123';

    await repository.saveSecret(
      id: id,
      label: label,
      value: value,
      derivedKey: derivedKey,
      salt: salt,
      iv: iv,
    );

    // 1. Check SecretEntries table
    final secrets = await db.getAllSecrets();
    expect(secrets.length, 1);
    expect(secrets.first.id, id);
    expect(secrets.first.label, label);

    // 2. Check physical file exists in /Secrets/
    final mirrorDir = Directory('${tempDir.path}/jarvis_mirror');
    final secretsFolder = Directory('${mirrorDir.path}/Secrets');
    final expectedFile = File('${secretsFolder.path}/$id.jvs');
    
    expect(expectedFile.existsSync(), isTrue);

    // 3. Check FileCacheEntries table (for sync engine visibility)
    final fileEntry = await db.getEntry('Secrets/$id.jvs');
    expect(fileEntry, isNotNull);
    expect(fileEntry!.path, 'Secrets/$id.jvs');
    expect(fileEntry.type, 'file');

    // 4. Check MutationQueue status
    final mutations = await db.getPendingMutations();
    expect(mutations.any((m) => m.path == 'Secrets/$id.jvs'), isTrue);
    expect(mutations.first.operation, 'update');

    // 5. Verify file content format (JVSP + salt + iv + ciphertext)
    final bytes = await expectedFile.readAsBytes();
    expect(bytes.sublist(0, 4), [74, 86, 83, 80]); // "JVSP"
    expect(bytes.sublist(4, 20), salt);
    expect(bytes.sublist(20, 32), iv);
    
    final ciphertext = bytes.sublist(32);
    final decrypted = cryptoService.decrypt(derivedKey, ciphertext, iv);
    expect(decrypted, value);
  });

  test('Deleting a secret removes DB entry, physical file, and enqueues delete mutation', () async {
    final derivedKey = Uint8List(32)..fillRange(0, 32, 1);
    final salt = Uint8List(16)..fillRange(0, 16, 2);
    final iv = Uint8List(12)..fillRange(0, 12, 3);
    
    const id = 'del-id';
    
    await repository.saveSecret(
      id: id,
      label: 'Delete Me',
      value: 'secret',
      derivedKey: derivedKey,
      salt: salt,
      iv: iv,
    );

    // Now delete
    await repository.deleteSecret(id);

    // 1. DB SecretEntries removed
    final secrets = await db.getAllSecrets();
    expect(secrets, isEmpty);

    // 2. Physical file removed
    final expectedFile = File('${tempDir.path}/jarvis_mirror/Secrets/$id.jvs');
    expect(expectedFile.existsSync(), isFalse);

    // 3. DB FileCacheEntries removed
    final fileEntry = await db.getEntry('Secrets/$id.jvs');
    expect(fileEntry, isNull);

    // 4. Delete mutation enqueued
    final mutations = await db.getPendingMutations();
    expect(mutations.any((m) => m.path == 'Secrets/$id.jvs' && m.operation == 'delete'), isTrue);
  });
}
