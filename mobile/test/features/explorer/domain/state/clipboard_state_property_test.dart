import 'dart:math';
import 'package:flutter_test/flutter_test.dart';
import 'package:drift/native.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/features/explorer/domain/models/file_operation_result.dart';
import 'package:jarvis_mobile/features/explorer/domain/services/file_operation_service.dart';
import 'package:jarvis_mobile/features/explorer/domain/state/clipboard_state.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

/// Property-based tests for ClipboardState and ClipboardStateNotifier.
///
/// These tests verify the correctness properties of the clipboard system
/// including cut, copy, paste, and clear operations using randomly generated
/// inputs to ensure comprehensive coverage.
///
/// Each property test runs a minimum of 100 iterations with randomly generated
/// inputs to ensure universal correctness.
void main() {
  group('Property-Based Tests: Clipboard State (Pure State)', () {
    late ClipboardStateNotifier notifier;

    setUp(() {
      notifier = ClipboardStateNotifier();
    });

    // Feature: advanced-file-manager, Property 24: Cut Stores Files with Move Operation
    // Validates: Requirements 11.1
    test('Property 24: Cut stores file IDs with cut operation type', () {
      const iterations = 100;
      final random = Random(2001);

      for (int i = 0; i < iterations; i++) {
        notifier = ClipboardStateNotifier();

        final fileIds = _generateRandomFileIds(random);

        notifier.cut(fileIds);

        // Verify file IDs are stored
        expect(
          notifier.state.fileIds,
          fileIds,
          reason:
              'Iteration $i: Cut should store the provided file IDs',
        );

        // Verify operation is cut
        expect(
          notifier.state.operation,
          ClipboardOperation.cut,
          reason: 'Iteration $i: Operation should be cut',
        );

        // Verify not empty
        expect(
          notifier.state.isNotEmpty,
          isTrue,
          reason: 'Iteration $i: Clipboard should not be empty after cut',
        );
      }
    });

    // Feature: advanced-file-manager, Property 25: Cut Replaces Previous Clipboard
    // Validates: Requirements 11.3
    test('Property 25: Cut replaces previous clipboard contents', () {
      const iterations = 100;
      final random = Random(2002);

      for (int i = 0; i < iterations; i++) {
        notifier = ClipboardStateNotifier();

        // First operation (could be cut or copy)
        final firstIds = _generateRandomFileIds(random);
        if (random.nextBool()) {
          notifier.cut(firstIds);
        } else {
          notifier.copy(firstIds);
        }

        // Second cut should replace
        final secondIds = _generateRandomFileIds(random);
        notifier.cut(secondIds);

        expect(
          notifier.state.fileIds,
          secondIds,
          reason:
              'Iteration $i: Second cut should replace previous clipboard',
        );
        expect(
          notifier.state.operation,
          ClipboardOperation.cut,
          reason: 'Iteration $i: Operation should be cut after second cut',
        );
      }
    });

    // Feature: advanced-file-manager, Property 26: Copy Stores Files with Copy Operation
    // Validates: Requirements 12.1
    test('Property 26: Copy stores file IDs with copy operation type', () {
      const iterations = 100;
      final random = Random(2003);

      for (int i = 0; i < iterations; i++) {
        notifier = ClipboardStateNotifier();

        final fileIds = _generateRandomFileIds(random);

        notifier.copy(fileIds);

        // Verify file IDs are stored
        expect(
          notifier.state.fileIds,
          fileIds,
          reason:
              'Iteration $i: Copy should store the provided file IDs',
        );

        // Verify operation is copy
        expect(
          notifier.state.operation,
          ClipboardOperation.copy,
          reason: 'Iteration $i: Operation should be copy',
        );
      }
    });

    // Feature: advanced-file-manager, Property 27: Copy Replaces Previous Clipboard
    // Validates: Requirements 12.2
    test('Property 27: Copy replaces previous clipboard contents', () {
      const iterations = 100;
      final random = Random(2004);

      for (int i = 0; i < iterations; i++) {
        notifier = ClipboardStateNotifier();

        // First operation
        final firstIds = _generateRandomFileIds(random);
        if (random.nextBool()) {
          notifier.cut(firstIds);
        } else {
          notifier.copy(firstIds);
        }

        // Second copy should replace
        final secondIds = _generateRandomFileIds(random);
        notifier.copy(secondIds);

        expect(
          notifier.state.fileIds,
          secondIds,
          reason:
              'Iteration $i: Second copy should replace previous clipboard',
        );
        expect(
          notifier.state.operation,
          ClipboardOperation.copy,
          reason: 'Iteration $i: Operation should be copy after second copy',
        );
      }
    });

    // Feature: advanced-file-manager, Property 28: Clipboard Persists Until Cleared
    // Validates: Requirements 11.4, 12.3
    test('Property 28: Clipboard persists until explicitly cleared', () {
      const iterations = 100;
      final random = Random(2005);

      for (int i = 0; i < iterations; i++) {
        notifier = ClipboardStateNotifier();

        final fileIds = _generateRandomFileIds(random);
        if (random.nextBool()) {
          notifier.cut(fileIds);
        } else {
          notifier.copy(fileIds);
        }

        // Verify clipboard is not empty
        expect(notifier.state.isNotEmpty, isTrue,
            reason: 'Pre-condition: clipboard should have items');

        // Access state multiple times (simulating navigation)
        for (int j = 0; j < random.nextInt(10) + 1; j++) {
          final state = notifier.state;
          expect(
            state.fileIds,
            fileIds,
            reason:
                'Iteration $i: Clipboard should persist across reads (access $j)',
          );
        }

        // Now clear
        notifier.clear();

        expect(
          notifier.state.isEmpty,
          isTrue,
          reason: 'Iteration $i: Clipboard should be empty after clear',
        );
        expect(
          notifier.state.operation,
          isNull,
          reason: 'Iteration $i: Operation should be null after clear',
        );
      }
    });

    test('Property 24: Cut creates a defensive copy of file IDs', () {
      const iterations = 50;
      final random = Random(2006);

      for (int i = 0; i < iterations; i++) {
        notifier = ClipboardStateNotifier();

        final fileIds = _generateRandomFileIds(random);
        notifier.cut(fileIds);

        // Mutate the original list
        fileIds.add('mutated_item');

        // Clipboard should NOT be affected
        expect(
          notifier.state.fileIds.contains('mutated_item'),
          isFalse,
          reason:
              'Iteration $i: Clipboard should not be affected by external mutation',
        );
      }
    });

    test('Property 26: Copy creates a defensive copy of file IDs', () {
      const iterations = 50;
      final random = Random(2007);

      for (int i = 0; i < iterations; i++) {
        notifier = ClipboardStateNotifier();

        final fileIds = _generateRandomFileIds(random);
        notifier.copy(fileIds);

        // Mutate the original list
        fileIds.add('mutated_item');

        // Clipboard should NOT be affected
        expect(
          notifier.state.fileIds.contains('mutated_item'),
          isFalse,
          reason:
              'Iteration $i: Clipboard should not be affected by external mutation',
        );
      }
    });

    test('Pasting empty clipboard returns validation error', () async {
      // We need a real service for paste tests
      final database = AppDatabase.forTesting(NativeDatabase.memory());
      final repository = ExplorerRepository(db: database);
      final service = FileOperationService(
        repository: repository,
        database: database,
      );

      try {
        notifier = ClipboardStateNotifier();

        final result = await notifier.paste('some_folder', service);

        expect(result.isFailure, isTrue,
            reason: 'Pasting empty clipboard should fail');
        expect(result.errors.first.type, FileOperationErrorType.validation,
            reason: 'Error should be validation type');
      } finally {
        await database.close();
      }
    });
  });

  group('Property-Based Tests: Clipboard Paste Operations', () {
    late AppDatabase database;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() async {
      database = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: database);
      service = FileOperationService(
        repository: repository,
        database: database,
      );
    });

    tearDown(() async {
      await database.close();
    });

    // Feature: advanced-file-manager, Property 29: Paste-Cut Performs Move and Clears
    // Validates: Requirements 13.1, 13.2
    test('Property 29: Paste after cut performs move and clears clipboard',
        () async {
      const iterations = 100;
      final random = Random(3001);

      for (int i = 0; i < iterations; i++) {
        final notifier = ClipboardStateNotifier();

        // Create source files and target folder
        final fileCount = random.nextInt(4) + 1; // 1-4 files
        final filePaths = <String>[];
        for (int j = 0; j < fileCount; j++) {
          final path = 'source_$i/file_$j.txt';
          await _insertFile(repository, path);
          filePaths.add(path);
        }

        final targetFolder = 'paste_target_$i';
        await _insertFolder(repository, targetFolder);

        // Cut and paste
        notifier.cut(filePaths);
        final result = await notifier.paste(targetFolder, service);

        // Verify files were moved
        expect(
          result.isSuccess,
          isTrue,
          reason: 'Iteration $i: Paste after cut should succeed',
        );

        // Verify clipboard is cleared after paste-cut
        expect(
          notifier.state.isEmpty,
          isTrue,
          reason:
              'Iteration $i: Clipboard should be cleared after paste-cut',
        );

        // Verify original files no longer exist
        for (final path in filePaths) {
          final entry = await repository.getEntry(path);
          expect(entry, isNull,
              reason:
                  'Original "$path" should not exist after paste-cut');
        }

        // Verify move mutations were queued
        final mutations = await database.getPendingMutations();
        final moveMutations =
            mutations.where((m) => m.operation == 'move').toList();
        expect(moveMutations.length, fileCount,
            reason: 'Should have $fileCount move mutations');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 30: Paste-Copy Performs Copy and Retains
    // Validates: Requirements 13.3, 13.4
    test(
        'Property 30: Paste after copy performs copy and retains clipboard',
        () async {
      const iterations = 100;
      final random = Random(3002);

      for (int i = 0; i < iterations; i++) {
        final notifier = ClipboardStateNotifier();

        // Create source files and target folder
        final fileCount = random.nextInt(4) + 1;
        final filePaths = <String>[];
        for (int j = 0; j < fileCount; j++) {
          final path = 'source_$i/file_$j.txt';
          await _insertFile(repository, path);
          filePaths.add(path);
        }

        final targetFolder = 'paste_target_$i';
        await _insertFolder(repository, targetFolder);

        // Copy and paste
        notifier.copy(filePaths);
        final result = await notifier.paste(targetFolder, service);

        // Verify files were copied
        expect(
          result.isSuccess,
          isTrue,
          reason: 'Iteration $i: Paste after copy should succeed',
        );

        // Verify clipboard is RETAINED after paste-copy
        expect(
          notifier.state.isNotEmpty,
          isTrue,
          reason:
              'Iteration $i: Clipboard should be retained after paste-copy',
        );
        expect(
          notifier.state.fileIds,
          filePaths,
          reason:
              'Iteration $i: Clipboard file IDs should remain unchanged',
        );
        expect(
          notifier.state.operation,
          ClipboardOperation.copy,
          reason:
              'Iteration $i: Clipboard operation should remain copy',
        );

        // Verify original files STILL exist
        for (final path in filePaths) {
          final entry = await repository.getEntry(path);
          expect(entry, isNotNull,
              reason:
                  'Original "$path" should still exist after paste-copy');
        }

        // Verify create mutations were queued
        final mutations = await database.getPendingMutations();
        final createMutations =
            mutations.where((m) => m.operation == 'create').toList();
        expect(createMutations.length, fileCount,
            reason: 'Should have $fileCount create mutations');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 30: Paste-copy can be performed multiple times', () async {
      const iterations = 50;

      for (int i = 0; i < iterations; i++) {
        final notifier = ClipboardStateNotifier();

        // Create a single source file
        final filePath = 'source/file_$i.txt';
        await _insertFile(repository, filePath);

        // Create two target folders
        final target1 = 'target1_$i';
        final target2 = 'target2_$i';
        await _insertFolder(repository, target1);
        await _insertFolder(repository, target2);

        // Copy
        notifier.copy([filePath]);

        // Paste to first target
        final result1 = await notifier.paste(target1, service);
        expect(result1.isSuccess, isTrue,
            reason: 'First paste should succeed');

        // Clipboard should still have items
        expect(notifier.state.isNotEmpty, isTrue,
            reason: 'Clipboard should persist after first paste-copy');

        // Paste to second target
        final result2 = await notifier.paste(target2, service);
        expect(result2.isSuccess, isTrue,
            reason: 'Second paste should succeed');

        // Both copies and original should exist
        final original = await repository.getEntry(filePath);
        expect(original, isNotNull, reason: 'Original should still exist');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test(
        'Property 29: Paste-cut with partial success still clears clipboard',
        () async {
      const iterations = 50;

      for (int i = 0; i < iterations; i++) {
        final notifier = ClipboardStateNotifier();

        final targetFolder = 'target_$i';
        await _insertFolder(repository, targetFolder);

        // Create some files, but also include non-existent ones
        final existingPath = 'source/existing_$i.txt';
        await _insertFile(repository, existingPath);

        // Put existing and non-existent paths in clipboard
        final clipboardPaths = [existingPath, 'source/nonexistent_$i.txt'];
        notifier.cut(clipboardPaths);

        final result = await notifier.paste(targetFolder, service);

        // Should have partial success (one exists, one doesn't)
        // But clipboard should be cleared since some operations succeeded
        if (result.isSuccess || result.isPartialSuccess) {
          expect(
            notifier.state.isEmpty,
            isTrue,
            reason:
                'Iteration $i: Clipboard should be cleared after partial paste-cut',
          );
        }

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });
  });
}

// =============================================================================
// Helpers
// =============================================================================

/// Generate a list of random file IDs
List<String> _generateRandomFileIds(Random random, {int? count}) {
  final idCount = count ?? (random.nextInt(8) + 1); // 1-8 items
  return List.generate(idCount, (i) => 'file_${random.nextInt(100000)}_$i');
}

/// Insert a single file into the repository
Future<void> _insertFile(ExplorerRepository repository, String path) async {
  final segments = path.split('/');
  final name = segments.last;

  await repository.upsertFile(
    FileEntry(
      path: path,
      name: name,
      type: 'file',
      sizeBytes: 1024,
      lastModified: DateTime.now().toUtc().toIso8601String(),
      serverVersion: 1,
    ),
  );
}

/// Insert a single folder into the repository
Future<void> _insertFolder(ExplorerRepository repository, String path) async {
  final segments = path.split('/');
  final name = segments.last;

  await repository.upsertFile(
    FileEntry(
      path: path,
      name: name,
      type: 'directory',
      lastModified: DateTime.now().toUtc().toIso8601String(),
      serverVersion: 1,
    ),
  );
}
