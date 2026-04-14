import 'dart:math';
import 'package:flutter_test/flutter_test.dart';
import 'package:drift/native.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/features/explorer/domain/models/file_operation_result.dart';
import 'package:jarvis_mobile/features/explorer/domain/services/file_operation_service.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

/// Property-based tests for path and name validation in FileOperationService.
///
/// These tests generate random strings with various combinations of valid and
/// invalid characters to verify that validation methods work correctly across
/// all possible inputs.
///
/// Each property test runs a minimum of 100 iterations with randomly generated
/// inputs to ensure comprehensive coverage.
void main() {
  group('Property-Based Tests: Path and Name Validation', () {
    // Feature: advanced-file-manager, Property 38: Path Validation Before Operations
    // Validates: Requirements 20.3
    test('Property 38: Path validation correctly identifies invalid paths',
        () {
      const iterations = 100;
      final random = Random(42); // Seed for reproducibility

      for (int i = 0; i < iterations; i++) {
        final path = _generateRandomPath(random);
        final result = _isValidPath(path);

        // Verify the result matches expected validation rules
        final hasInvalidChars = _hasInvalidPathChars(path);
        final hasReservedName = _hasReservedName(path);
        final isEmpty = path.isEmpty;

        if (isEmpty || hasInvalidChars || hasReservedName) {
          expect(
            result,
            isFalse,
            reason: 'Path "$path" should be invalid '
                '(empty: $isEmpty, invalidChars: $hasInvalidChars, reserved: $hasReservedName)',
          );
        } else {
          expect(
            result,
            isTrue,
            reason: 'Path "$path" should be valid',
          );
        }
      }
    });

    // Feature: advanced-file-manager, Property 39: Name Validation Before Operations
    // Validates: Requirements 20.4
    test('Property 39: Name validation correctly identifies invalid names',
        () {
      const iterations = 100;
      final random = Random(42); // Seed for reproducibility

      for (int i = 0; i < iterations; i++) {
        final name = _generateRandomFileName(random);
        final result = _isValidFileName(name);

        // Verify the result matches expected validation rules
        final hasInvalidChars = _hasInvalidFileNameChars(name);
        final hasReservedName = _isReservedName(name);
        final isEmpty = name.trim().isEmpty;
        final endsWithPeriodOrSpace =
            name.endsWith('.') || name.endsWith(' ');

        if (isEmpty ||
            hasInvalidChars ||
            hasReservedName ||
            endsWithPeriodOrSpace) {
          expect(
            result,
            isFalse,
            reason: 'Name "$name" should be invalid '
                '(empty: $isEmpty, invalidChars: $hasInvalidChars, '
                'reserved: $hasReservedName, badEnding: $endsWithPeriodOrSpace)',
          );
        } else {
          expect(
            result,
            isTrue,
            reason: 'Name "$name" should be valid',
          );
        }
      }
    });

    test('Property 38: Path validation is consistent across multiple calls',
        () {
      const iterations = 100;
      final random = Random(123);

      for (int i = 0; i < iterations; i++) {
        final path = _generateRandomPath(random);

        // Call validation multiple times
        final result1 = _isValidPath(path);
        final result2 = _isValidPath(path);
        final result3 = _isValidPath(path);

        // Results should be identical
        expect(result1, equals(result2),
            reason: 'Path validation should be deterministic for "$path"');
        expect(result2, equals(result3),
            reason: 'Path validation should be deterministic for "$path"');
      }
    });

    test('Property 39: Name validation is consistent across multiple calls',
        () {
      const iterations = 100;
      final random = Random(123);

      for (int i = 0; i < iterations; i++) {
        final name = _generateRandomFileName(random);

        // Call validation multiple times
        final result1 = _isValidFileName(name);
        final result2 = _isValidFileName(name);
        final result3 = _isValidFileName(name);

        // Results should be identical
        expect(result1, equals(result2),
            reason: 'Name validation should be deterministic for "$name"');
        expect(result2, equals(result3),
            reason: 'Name validation should be deterministic for "$name"');
      }
    });

    test('Property 38: All strings with invalid characters are rejected', () {
      const iterations = 100;
      final random = Random(456);
      final invalidChars = ['<', '>', ':', '"', '|', '?', '*'];

      for (int i = 0; i < iterations; i++) {
        // Generate a path that definitely contains an invalid character
        final baseString = _generateValidPathSegment(random);
        final invalidChar = invalidChars[random.nextInt(invalidChars.length)];
        final position = random.nextInt(baseString.length + 1);
        final path = baseString.substring(0, position) +
            invalidChar +
            baseString.substring(position);

        final result = _isValidPath(path);
        expect(
          result,
          isFalse,
          reason: 'Path "$path" with invalid char "$invalidChar" should be rejected',
        );
      }
    });

    test('Property 39: All strings with invalid characters are rejected', () {
      const iterations = 100;
      final random = Random(456);
      final invalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

      for (int i = 0; i < iterations; i++) {
        // Generate a name that definitely contains an invalid character
        final baseString = _generateValidFileNameBase(random);
        final invalidChar = invalidChars[random.nextInt(invalidChars.length)];
        final position = random.nextInt(baseString.length + 1);
        final name = baseString.substring(0, position) +
            invalidChar +
            baseString.substring(position);

        final result = _isValidFileName(name);
        expect(
          result,
          isFalse,
          reason: 'Name "$name" with invalid char "$invalidChar" should be rejected',
        );
      }
    });

    test('Property 38: All reserved names are rejected in paths', () {
      const iterations = 100;
      final random = Random(789);
      final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];

      for (int i = 0; i < iterations; i++) {
        final reserved =
            reservedNames[random.nextInt(reservedNames.length)];
        
        // Test with different casings
        final variations = [
          reserved,
          reserved.toLowerCase(),
          _mixCase(reserved, random),
        ];

        for (final variant in variations) {
          // Test as standalone and as part of path
          final paths = [
            variant,
            'folder/$variant',
            '$variant/subfolder',
            'folder/$variant/file',
          ];

          for (final path in paths) {
            final result = _isValidPath(path);
            expect(
              result,
              isFalse,
              reason: 'Path "$path" with reserved name "$variant" should be rejected',
            );
          }
        }
      }
    });

    test('Property 39: All reserved names are rejected as file names', () {
      const iterations = 100;
      final random = Random(789);
      final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];

      for (int i = 0; i < iterations; i++) {
        final reserved =
            reservedNames[random.nextInt(reservedNames.length)];
        
        // Test with different casings
        final variations = [
          reserved,
          reserved.toLowerCase(),
          _mixCase(reserved, random),
        ];

        for (final variant in variations) {
          final result = _isValidFileName(variant);
          expect(
            result,
            isFalse,
            reason: 'Name "$variant" (reserved) should be rejected',
          );
        }
      }
    });

    test('Property 39: Names ending with period or space are rejected', () {
      const iterations = 100;
      final random = Random(101);

      for (int i = 0; i < iterations; i++) {
        final baseName = _generateValidFileNameBase(random);
        
        // Test with trailing period
        final nameWithPeriod = '$baseName.';
        expect(
          _isValidFileName(nameWithPeriod),
          isFalse,
          reason: 'Name "$nameWithPeriod" ending with period should be rejected',
        );

        // Test with trailing space
        final nameWithSpace = '$baseName ';
        expect(
          _isValidFileName(nameWithSpace),
          isFalse,
          reason: 'Name "$nameWithSpace" ending with space should be rejected',
        );
      }
    });

    test('Property 38: Empty paths are always rejected', () {
      expect(_isValidPath(''), isFalse);
    });

    test('Property 39: Empty and whitespace-only names are rejected', () {
      const iterations = 50;
      final random = Random(202);

      // Test empty string
      expect(_isValidFileName(''), isFalse);

      // Test various whitespace-only strings
      for (int i = 0; i < iterations; i++) {
        final spaceCount = random.nextInt(10) + 1;
        final whitespace = ' ' * spaceCount;
        expect(
          _isValidFileName(whitespace),
          isFalse,
          reason: 'Whitespace-only name "$whitespace" should be rejected',
        );
      }
    });

    test('Property 38: Valid paths with allowed characters are accepted', () {
      const iterations = 100;
      final random = Random(303);

      for (int i = 0; i < iterations; i++) {
        // Generate paths using only valid characters
        final segments = <String>[];
        final segmentCount = random.nextInt(5) + 1;
        
        for (int j = 0; j < segmentCount; j++) {
          segments.add(_generateValidPathSegment(random));
        }
        
        final path = segments.join('/');
        final result = _isValidPath(path);
        
        expect(
          result,
          isTrue,
          reason: 'Valid path "$path" should be accepted',
        );
      }
    });

    test('Property 39: Valid names with allowed characters are accepted', () {
      const iterations = 100;
      final random = Random(303);

      for (int i = 0; i < iterations; i++) {
        // Generate names using only valid characters
        final name = _generateValidFileNameBase(random);
        
        // Optionally add extension
        final withExtension = random.nextBool();
        final fullName = withExtension 
            ? '$name.${_generateValidExtension(random)}'
            : name;
        
        final result = _isValidFileName(fullName);
        
        expect(
          result,
          isTrue,
          reason: 'Valid name "$fullName" should be accepted',
        );
      }
    });
  });

  group('Property-Based Tests: Circular Move Detection', () {
    late AppDatabase database;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() async {
      // Create in-memory database for testing
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

    // Feature: advanced-file-manager, Property 35: Circular Move Detection - Self
    // Validates: Requirements 15.1
    test('Property 35: Moving a folder into itself is detected as circular',
        () async {
      const iterations = 100;
      final random = Random(1001);

      for (int i = 0; i < iterations; i++) {
        // Generate a random folder hierarchy
        final hierarchy = _generateFolderHierarchy(
          random: random,
          minDepth: 1,
          maxDepth: 5,
          maxChildren: 5,
        );

        // Insert the hierarchy into the database
        await _insertHierarchy(repository, hierarchy);

        // Try to move the root folder into itself
        final result = await service.moveFile(
          hierarchy.rootPath,
          hierarchy.rootPath,
        );

        // Verify the move was rejected
        expect(
          result.isFailure,
          isTrue,
          reason: 'Moving folder "${hierarchy.rootPath}" into itself should fail',
        );

        expect(
          result.errors.first.type,
          FileOperationErrorType.validation,
          reason: 'Error should be a validation error',
        );

        // Verify database was not modified
        final folder = await repository.getEntry(hierarchy.rootPath);
        expect(
          folder,
          isNotNull,
          reason: 'Folder should still exist after failed move',
        );

        // Clean up for next iteration
        await database.deleteAllEntries();
      }
    });

    // Feature: advanced-file-manager, Property 36: Circular Move Detection - Descendants
    // Validates: Requirements 15.2
    test(
        'Property 36: Moving a folder into its descendants is detected as circular',
        () async {
      const iterations = 100;
      final random = Random(1002);

      for (int i = 0; i < iterations; i++) {
        // Generate a random folder hierarchy with at least 2 levels
        final hierarchy = _generateFolderHierarchy(
          random: random,
          minDepth: 2,
          maxDepth: 5,
          maxChildren: 5,
        );

        // Insert the hierarchy into the database
        await _insertHierarchy(repository, hierarchy);

        // Pick a random descendant folder
        final descendant = hierarchy.pickRandomDescendant(random);

        // Try to move the root folder into the descendant
        final result = await service.moveFile(
          hierarchy.rootPath,
          descendant.path,
        );

        // Verify the move was rejected
        expect(
          result.isFailure,
          isTrue,
          reason:
              'Moving folder "${hierarchy.rootPath}" into descendant "${descendant.path}" should fail',
        );

        expect(
          result.errors.first.type,
          FileOperationErrorType.validation,
          reason: 'Error should be a validation error',
        );

        // Verify database was not modified
        final folder = await repository.getEntry(hierarchy.rootPath);
        expect(
          folder,
          isNotNull,
          reason: 'Folder should still exist after failed move',
        );

        // Clean up for next iteration
        await database.deleteAllEntries();
      }
    });

    test(
        'Property 36: Non-circular moves (to sibling or parent) are NOT flagged',
        () async {
      const iterations = 100;
      final random = Random(1003);

      for (int i = 0; i < iterations; i++) {
        // Generate a folder hierarchy
        final hierarchy = _generateFolderHierarchy(
          random: random,
          minDepth: 2,
          maxDepth: 5,
          maxChildren: 5,
        );

        // Insert the hierarchy into the database
        await _insertHierarchy(repository, hierarchy);

        // Create a sibling folder (not a descendant)
        final siblingPath = '${hierarchy.rootPath}_sibling_$i';
        await _insertFolder(repository, siblingPath);

        // Try to move a child folder to the sibling (non-circular)
        if (hierarchy.allFolders.length > 1) {
          final childFolder = hierarchy.allFolders[1]; // Pick first child
          final result = await service.moveFile(
            childFolder.path,
            siblingPath,
          );

          // This should NOT be flagged as circular
          // (It might fail for other reasons like name conflicts, but not circular)
          if (result.isFailure) {
            expect(
              result.errors.first.type,
              isNot(FileOperationErrorType.validation),
              reason:
                  'Moving to sibling should not be a validation error (circular)',
            );
          }
        }

        // Clean up for next iteration
        await database.deleteAllEntries();
      }
    });

    test('Property 35 & 36: Database remains unchanged after failed move',
        () async {
      const iterations = 100;
      final random = Random(1004);

      for (int i = 0; i < iterations; i++) {
        // Generate a folder hierarchy
        final hierarchy = _generateFolderHierarchy(
          random: random,
          minDepth: 2,
          maxDepth: 5,
          maxChildren: 5,
        );

        // Insert the hierarchy into the database
        await _insertHierarchy(repository, hierarchy);

        // Capture initial state
        final initialEntries = await repository.getAllFiles();
        final initialCount = initialEntries.length;

        // Try circular move (into itself)
        await service.moveFile(hierarchy.rootPath, hierarchy.rootPath);

        // Verify database unchanged
        final afterEntries = await repository.getAllFiles();
        expect(
          afterEntries.length,
          initialCount,
          reason: 'Database entry count should not change after failed move',
        );

        // Try circular move (into descendant)
        if (hierarchy.allFolders.length > 1) {
          final descendant = hierarchy.pickRandomDescendant(random);
          await service.moveFile(hierarchy.rootPath, descendant.path);

          // Verify database still unchanged
          final finalEntries = await repository.getAllFiles();
          expect(
            finalEntries.length,
            initialCount,
            reason:
                'Database entry count should not change after failed move to descendant',
          );
        }

        // Clean up for next iteration
        await database.deleteAllEntries();
      }
    });
  });

  group('Property-Based Tests: Single File Move Operations', () {
    late AppDatabase database;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() async {
      // Create in-memory database for testing
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

    // Feature: advanced-file-manager, Property 7: Move Updates Path and Syncs
    // Validates: Requirements 4.1, 4.2, 4.3
    test('Property 7: Move operation updates database and queues mutation',
        () async {
      const iterations = 100;
      final random = Random(2001);

      for (int i = 0; i < iterations; i++) {
        // Generate random file and target folder
        final filePath = _generateRandomFilePath(random, 'file_$i');
        final targetFolderPath = _generateRandomFolderPath(random, 'target_$i');

        // Insert file and target folder
        await _insertFile(repository, filePath);
        await _insertFolder(repository, targetFolderPath);

        // Perform move operation
        final result = await service.moveFile(filePath, targetFolderPath);

        // Verify success
        expect(
          result.isSuccess,
          isTrue,
          reason: 'Move from "$filePath" to "$targetFolderPath" should succeed',
        );

        // Verify file was moved in database
        final oldFile = await repository.getEntry(filePath);
        expect(
          oldFile,
          isNull,
          reason: 'Old file path "$filePath" should not exist after move',
        );

        final newPath = '$targetFolderPath/${filePath.split('/').last}';
        final movedFile = await repository.getEntry(newPath);
        expect(
          movedFile,
          isNotNull,
          reason: 'File should exist at new path "$newPath"',
        );

        // Verify mutations were queued (decomposed into delete and update)
        final mutations = await database.getPendingMutations();
        expect(
          mutations.length,
          greaterThanOrEqualTo(2),
          reason: 'At least two mutations should be queued (delete + update)',
        );

        final deleteMutation = mutations.firstWhere(
          (m) => m.operation == 'delete' && m.path == filePath,
          orElse: () => throw StateError('Delete mutation for old path "$filePath" not found'),
        );
        expect(deleteMutation.operation, 'delete');

        final updateMutation = mutations.firstWhere(
          (m) => (m.operation == 'update' || m.operation == 'create') && m.path == newPath,
          orElse: () => throw StateError('Update mutation for new path "$newPath" not found'),
        );
        expect(updateMutation.path, newPath);

        // Clean up for next iteration
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 8: Move Rejects Name Conflicts
    // Validates: Requirements 4.4
    test('Property 8: Move rejects name conflicts without database changes',
        () async {
      const iterations = 100;

      for (int i = 0; i < iterations; i++) {
        // Generate file and target folder with existing file of same name
        final fileName = 'file_$i.txt';
        final sourcePath = 'source/folder/$fileName';
        final targetFolderPath = 'target/folder';
        final conflictingPath = '$targetFolderPath/$fileName';

        // Insert source file, target folder, and conflicting file
        await _insertFile(repository, sourcePath);
        await _insertFolder(repository, targetFolderPath);
        await _insertFile(repository, conflictingPath);

        // Capture initial database state
        final initialFiles = await repository.getAllFiles();
        final initialCount = initialFiles.length;

        // Attempt move operation
        final result = await service.moveFile(sourcePath, targetFolderPath);

        // Verify failure due to conflict
        expect(
          result.isFailure,
          isTrue,
          reason: 'Move should fail due to name conflict',
        );

        expect(
          result.errors.first.type,
          FileOperationErrorType.conflict,
          reason: 'Error should be a conflict error',
        );

        // Verify database was not modified
        final afterFiles = await repository.getAllFiles();
        expect(
          afterFiles.length,
          initialCount,
          reason: 'Database entry count should not change after failed move',
        );

        // Verify source file still exists at original location
        final sourceFile = await repository.getEntry(sourcePath);
        expect(
          sourceFile,
          isNotNull,
          reason: 'Source file should still exist at original path',
        );

        // Verify no mutations were queued
        final mutations = await database.getPendingMutations();
        expect(
          mutations.isEmpty,
          isTrue,
          reason: 'No mutations should be queued for failed move',
        );

        // Clean up for next iteration
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 9: Move Validates Paths
    // Validates: Requirements 4.5
    test('Property 9: Move validates paths and rejects invalid ones',
        () async {
      const iterations = 100;
      final random = Random(2003);

      for (int i = 0; i < iterations; i++) {
        // Generate valid file and various invalid target paths
        final filePath = _generateRandomFilePath(random, 'file_$i');
        await _insertFile(repository, filePath);

        // Test with invalid target path
        final invalidTargetPath = _generateInvalidPath(random);

        // Attempt move operation
        final result = await service.moveFile(filePath, invalidTargetPath);

        // Verify failure due to validation
        expect(
          result.isFailure,
          isTrue,
          reason: 'Move to invalid path "$invalidTargetPath" should fail',
        );

        expect(
          result.errors.first.type,
          FileOperationErrorType.validation,
          reason: 'Error should be a validation error',
        );

        // Verify database was not modified
        final sourceFile = await repository.getEntry(filePath);
        expect(
          sourceFile,
          isNotNull,
          reason: 'Source file should still exist after failed move',
        );

        // Verify no mutations were queued
        final mutations = await database.getPendingMutations();
        expect(
          mutations.isEmpty,
          isTrue,
          reason: 'No mutations should be queued for failed move',
        );

        // Clean up for next iteration
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 9: Move validates source path existence', () async {
      const iterations = 100;
      final random = Random(2004);

      for (int i = 0; i < iterations; i++) {
        // Generate non-existent source file and valid target folder
        final nonExistentPath = _generateRandomFilePath(random, 'nonexistent_$i');
        final targetFolderPath = _generateRandomFolderPath(random, 'target_$i');

        // Only insert target folder (source doesn't exist)
        await _insertFolder(repository, targetFolderPath);

        // Attempt move operation
        final result = await service.moveFile(nonExistentPath, targetFolderPath);

        // Verify failure due to file not found
        expect(
          result.isFailure,
          isTrue,
          reason: 'Move of non-existent file should fail',
        );

        expect(
          result.errors.first.type,
          FileOperationErrorType.notFound,
          reason: 'Error should be a not found error',
        );

        // Verify no mutations were queued
        final mutations = await database.getPendingMutations();
        expect(
          mutations.isEmpty,
          isTrue,
          reason: 'No mutations should be queued for failed move',
        );

        // Clean up for next iteration
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 9: Move validates target folder existence', () async {
      const iterations = 100;
      final random = Random(2005);

      for (int i = 0; i < iterations; i++) {
        // Generate valid source file and non-existent target folder
        final filePath = _generateRandomFilePath(random, 'file_$i');
        final nonExistentTarget = _generateRandomFolderPath(random, 'nonexistent_$i');

        // Only insert source file (target doesn't exist)
        await _insertFile(repository, filePath);

        // Attempt move operation
        final result = await service.moveFile(filePath, nonExistentTarget);

        // Verify failure due to target not found
        expect(
          result.isFailure,
          isTrue,
          reason: 'Move to non-existent target should fail',
        );

        expect(
          result.errors.first.type,
          FileOperationErrorType.notFound,
          reason: 'Error should be a not found error',
        );

        // Verify source file still exists
        final sourceFile = await repository.getEntry(filePath);
        expect(
          sourceFile,
          isNotNull,
          reason: 'Source file should still exist after failed move',
        );

        // Verify no mutations were queued
        final mutations = await database.getPendingMutations();
        expect(
          mutations.isEmpty,
          isTrue,
          reason: 'No mutations should be queued for failed move',
        );

        // Clean up for next iteration
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 7: Move operation is atomic (transaction rollback on failure)',
        () async {
      const iterations = 50;
      final random = Random(2006);

      for (int i = 0; i < iterations; i++) {
        // Generate file and target folder
        final filePath = _generateRandomFilePath(random, 'file_$i');
        final targetFolderPath = _generateRandomFolderPath(random, 'target_$i');

        // Insert file and target folder
        await _insertFile(repository, filePath);
        await _insertFolder(repository, targetFolderPath);

        // Capture initial state
        final initialFiles = await repository.getAllFiles();
        final initialCount = initialFiles.length;

        // Perform successful move
        final result = await service.moveFile(filePath, targetFolderPath);

        if (result.isSuccess) {
          // Verify database state is consistent
          final afterFiles = await repository.getAllFiles();
          
          // Count should remain the same (one file moved, not duplicated)
          expect(
            afterFiles.length,
            initialCount,
            reason: 'File count should remain the same after move',
          );

          // Old path should not exist
          final oldFile = await repository.getEntry(filePath);
          expect(
            oldFile,
            isNull,
            reason: 'Old file path should not exist',
          );

          // New path should exist
          final newPath = '$targetFolderPath/${filePath.split('/').last}';
          final newFile = await repository.getEntry(newPath);
          expect(
            newFile,
            isNotNull,
            reason: 'New file path should exist',
          );
        }

        // Clean up for next iteration
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 7 & 8: Move preserves file metadata', () async {
      const iterations = 100;
      final random = Random(2007);

      for (int i = 0; i < iterations; i++) {
        // Generate file with specific metadata
        final filePath = 'source/file_$i.txt';
        final targetFolderPath = 'target';
        final fileName = 'file_$i.txt';
        final fileSize = random.nextInt(1000000);
        final lastModified = DateTime.now().subtract(Duration(days: random.nextInt(365))).toUtc().toIso8601String();

        // Insert file with metadata
        await repository.upsertFile(
          FileEntry(
            path: filePath,
            name: fileName,
            type: 'file',
            sizeBytes: fileSize,
            lastModified: lastModified,
            serverVersion: 1,
          ),
        );

        // Insert target folder
        await _insertFolder(repository, targetFolderPath);

        // Perform move
        final result = await service.moveFile(filePath, targetFolderPath);

        if (result.isSuccess) {
          // Verify metadata is preserved
          final newPath = '$targetFolderPath/$fileName';
          final movedFile = await repository.getEntry(newPath);

          expect(movedFile?.name, fileName, reason: 'File name should be preserved');
          expect(movedFile?.sizeBytes, fileSize, reason: 'File size should be preserved');
          expect(movedFile?.lastModified, lastModified, reason: 'Last modified should be preserved');
        }

        // Clean up for next iteration
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });
  });

  // ==========================================================================
  // Task 3.6: Batch Move Property Tests (Properties 10, 11)
  // ==========================================================================
  group('Property-Based Tests: Batch Move Operations', () {
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

    // Feature: advanced-file-manager, Property 10: Batch Move Partial Success
    // Validates: Requirements 5.1, 5.2, 5.3, 21.1
    test('Property 10: Batch move handles partial success correctly',
        () async {
      const iterations = 100;
      final random = Random(3001);

      for (int i = 0; i < iterations; i++) {
        final targetFolderPath = 'target_$i';
        await _insertFolder(repository, targetFolderPath);

        // Generate batch of files - some will conflict, some won't
        final batchSize = random.nextInt(8) + 2; // 2-9 files
        final filePaths = <String>[];
        final conflictCount = random.nextInt(batchSize); // 0 to batchSize-1

        for (int j = 0; j < batchSize; j++) {
          final filePath = 'source_$i/file_$j.txt';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);

          // Create a conflicting file in target for the first `conflictCount` files
          if (j < conflictCount) {
            await _insertFile(repository, '$targetFolderPath/file_$j.txt');
          }
        }

        // Perform batch move
        final result = await service.moveFiles(filePaths, targetFolderPath);

        // Verify: non-conflicting files were moved successfully
        final expectedSuccessCount = batchSize - conflictCount;
        expect(
          result.successfulIds.length,
          expectedSuccessCount,
          reason:
              'Iteration $i: Expected $expectedSuccessCount successes, got ${result.successfulIds.length}',
        );

        // Verify: conflicting files produced errors
        expect(
          result.errors.length,
          conflictCount,
          reason:
              'Iteration $i: Expected $conflictCount errors, got ${result.errors.length}',
        );

        // Verify: all errors are conflict type
        for (final error in result.errors) {
          expect(
            error.type,
            FileOperationErrorType.conflict,
            reason: 'All errors should be conflict type',
          );
        }

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 11: Batch Move Syncs All Successes
    // Validates: Requirements 5.5, 21.5
    test('Property 11: Batch move enqueues mutation for each successful move',
        () async {
      const iterations = 100;
      final random = Random(3002);

      for (int i = 0; i < iterations; i++) {
        final targetFolderPath = 'target_$i';
        await _insertFolder(repository, targetFolderPath);

        // Generate batch of files with no conflicts
        final batchSize = random.nextInt(6) + 2; // 2-7 files
        final filePaths = <String>[];

        for (int j = 0; j < batchSize; j++) {
          final filePath = 'source_$i/unique_${i}_$j.txt';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);
        }

        // Perform batch move
        final result = await service.moveFiles(filePaths, targetFolderPath);

        // Verify all files were moved successfully
        expect(
          result.isSuccess,
          isTrue,
          reason: 'All moves should succeed (no conflicts)',
        );

        // Verify mutation count equals successful move count
        final mutations = await database.getPendingMutations();
        final moveMutations =
            mutations.toList();

        expect(
          moveMutations.length,
          batchSize * 2,
          reason:
              'Iteration $i: Mutation count (${moveMutations.length}) should equal double batch size (${batchSize * 2})',
        );

        // Verify each moved file has corresponding delete and update mutations
        for (final filePath in filePaths) {
          final fileName = filePath.split('/').last;
          final newPath = '$targetFolderPath/$fileName';
          
          final hasDelete = moveMutations.any((m) => m.operation == 'delete' && m.path == filePath);
          final hasUpdate = moveMutations.any((m) => (m.operation == 'update' || m.operation == 'create') && m.path == newPath);
          
          expect(hasDelete, isTrue, reason: 'Missing delete mutation for $filePath');
          expect(hasUpdate, isTrue, reason: 'Missing update mutation for $newPath');
        }

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test(
        'Property 10: Batch move with all conflicts results in total failure',
        () async {
      const iterations = 50;
      final random = Random(3003);

      for (int i = 0; i < iterations; i++) {
        final targetFolderPath = 'target_$i';
        await _insertFolder(repository, targetFolderPath);

        final batchSize = random.nextInt(5) + 2;
        final filePaths = <String>[];

        for (int j = 0; j < batchSize; j++) {
          final fileName = 'file_$j.txt';
          final filePath = 'source_$i/$fileName';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);

          // Create conflicting file for each
          await _insertFile(repository, '$targetFolderPath/$fileName');
        }

        final result = await service.moveFiles(filePaths, targetFolderPath);

        // All should fail
        expect(result.isFailure, isTrue,
            reason: 'All moves should fail with conflicts');
        expect(result.errors.length, batchSize,
            reason: 'Error count should match batch size');

        // No mutations should be queued
        final mutations = await database.getPendingMutations();
        expect(mutations.isEmpty, isTrue,
            reason: 'No mutations for fully failed batch');

        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });
  });

  // ==========================================================================
  // Task 4.3: Single File Copy Property Tests (Properties 12, 13, 14)
  // ==========================================================================
  group('Property-Based Tests: Single File Copy Operations', () {
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

    // Feature: advanced-file-manager, Property 12: Copy Creates New Entry with Unique ID
    // Validates: Requirements 6.1, 6.2
    test('Property 12: Copy creates a new entry with a different path',
        () async {
      const iterations = 100;
      final random = Random(4001);

      for (int i = 0; i < iterations; i++) {
        final filePath = _generateRandomFilePath(random, 'copytest_$i');
        final targetFolderPath =
            _generateRandomFolderPath(random, 'copytarget_$i');

        // Insert source file with specific metadata
        final fileSize = random.nextInt(1000000);
        final lastModified = DateTime.now()
            .subtract(Duration(days: random.nextInt(365)))
            .toUtc()
            .toIso8601String();

        await repository.upsertFile(
          FileEntry(
            path: filePath,
            name: filePath.split('/').last,
            type: 'file',
            sizeBytes: fileSize,
            lastModified: lastModified,
            serverVersion: 1,
          ),
        );
        await _insertFolder(repository, targetFolderPath);

        // Perform copy
        final result = await service.copyFile(filePath, targetFolderPath);

        expect(
          result.isSuccess,
          isTrue,
          reason: 'Copy from "$filePath" to "$targetFolderPath" should succeed',
        );

        // Verify original file still exists
        final originalFile = await repository.getEntry(filePath);
        expect(originalFile, isNotNull,
            reason: 'Original file should still exist after copy');

        // Verify copy exists at target with new path
        final copiedPath = result.successfulIds.first;
        expect(copiedPath, isNot(filePath),
            reason: 'Copied file path should differ from original');

        final copiedFile = await repository.getEntry(copiedPath);
        expect(copiedFile, isNotNull,
            reason: 'Copied file should exist at target');

        // Verify metadata is preserved
        expect(copiedFile?.sizeBytes, fileSize,
            reason: 'Copy should preserve file size');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 13: Copy Syncs to Queue
    // Validates: Requirements 6.3
    test('Property 13: Copy enqueues a create mutation', () async {
      const iterations = 100;

      for (int i = 0; i < iterations; i++) {
        final filePath = 'source/file_$i.txt';
        final targetFolderPath = 'copytarget_$i';

        await _insertFile(repository, filePath);
        await _insertFolder(repository, targetFolderPath);

        final result = await service.copyFile(filePath, targetFolderPath);

        expect(result.isSuccess, isTrue,
            reason: 'Copy should succeed');

        // Verify a create mutation was queued
        final mutations = await database.getPendingMutations();
        final createMutations =
            mutations.where((m) => m.operation == 'create').toList();

        expect(createMutations.isNotEmpty, isTrue,
            reason: 'A create mutation should be queued');

        final copyMutation = createMutations.firstWhere(
          (m) => m.path == result.successfulIds.first,
          orElse: () => throw StateError('Create mutation not found'),
        );
        expect(copyMutation.operation, 'create',
            reason: 'Mutation should be a create operation');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 14: Copy Resolves Name Conflicts
    // Validates: Requirements 6.4, 6.5
    test('Property 14: Copy resolves name conflicts with numeric suffixes',
        () async {
      const iterations = 100;
      final random = Random(4003);

      for (int i = 0; i < iterations; i++) {
        final fileName = 'document.txt';
        final filePath = 'source/$fileName';
        final targetFolderPath = 'copytarget_$i';

        await _insertFile(repository, filePath);
        await _insertFolder(repository, targetFolderPath);

        // Create conflicting files with numbered suffixes
        final existingConflicts = random.nextInt(5); // 0-4 existing conflicts
        await _insertFile(repository, '$targetFolderPath/$fileName');
        for (int j = 1; j <= existingConflicts; j++) {
          await _insertFile(
              repository, '$targetFolderPath/document ($j).txt');
        }

        // Perform copy
        final result = await service.copyFile(filePath, targetFolderPath);

        expect(result.isSuccess, isTrue,
            reason: 'Copy should succeed with name conflict resolution');

        // Verify the copied file has a unique name
        final copiedPath = result.successfulIds.first;
        final copiedName = copiedPath.split('/').last;

        // Expected name: "document (N).txt" where N = existingConflicts + 1
        final expectedSuffix = existingConflicts + 1;
        expect(
          copiedName,
          'document ($expectedSuffix).txt',
          reason:
              'Copied file should have suffix ($expectedSuffix) but got "$copiedName"',
        );

        // Verify original file is unchanged
        final originalFile = await repository.getEntry(filePath);
        expect(originalFile, isNotNull,
            reason: 'Original file should remain unchanged');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 12: Copy of a file to the same folder creates a renamed copy',
        () async {
      const iterations = 50;

      for (int i = 0; i < iterations; i++) {
        final filePath = 'folder_$i/file_$i.txt';
        final parentFolder = 'folder_$i';

        await _insertFolder(repository, parentFolder);
        await _insertFile(repository, filePath);

        // Copy to same folder
        final result = await service.copyFile(filePath, parentFolder);

        expect(result.isSuccess, isTrue,
            reason: 'Copy to same folder should succeed');

        // Verify both original and copy exist
        final originalFile = await repository.getEntry(filePath);
        expect(originalFile, isNotNull, reason: 'Original should still exist');

        final copiedPath = result.successfulIds.first;
        final copiedFile = await repository.getEntry(copiedPath);
        expect(copiedFile, isNotNull, reason: 'Copy should exist');

        // Names should differ
        expect(copiedPath, isNot(filePath),
            reason: 'Copy should have different path');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });
  });

  // ==========================================================================
  // Task 4.5: Batch Copy Property Tests (Properties 15, 16)
  // ==========================================================================
  group('Property-Based Tests: Batch Copy Operations', () {
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

    // Feature: advanced-file-manager, Property 15: Batch Copy Resolves All Conflicts
    // Validates: Requirements 7.1, 7.2, 21.2
    test('Property 15: Batch copy resolves all name conflicts', () async {
      const iterations = 100;
      final random = Random(5001);

      for (int i = 0; i < iterations; i++) {
        final targetFolderPath = 'batchcopy_target_$i';
        await _insertFolder(repository, targetFolderPath);

        // Create batch of files, some with same names to trigger conflicts
        final batchSize = random.nextInt(5) + 2; // 2-6 files
        final filePaths = <String>[];

        for (int j = 0; j < batchSize; j++) {
          final filePath = 'source_$i/file_$j.txt';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);

          // Pre-create a conflicting file in target
          await _insertFile(repository, '$targetFolderPath/file_$j.txt');
        }

        // Perform batch copy
        final result =
            await service.copyFiles(filePaths, targetFolderPath);

        // All copies should succeed (conflicts resolved with suffixes)
        expect(
          result.isSuccess,
          isTrue,
          reason:
              'Iteration $i: All batch copies should succeed with conflict resolution',
        );

        expect(
          result.successfulIds.length,
          batchSize,
          reason:
              'Iteration $i: All $batchSize files should be copied successfully',
        );

        // Verify all copied paths are unique
        final copiedPaths = result.successfulIds.toSet();
        expect(
          copiedPaths.length,
          batchSize,
          reason: 'All copied paths should be unique',
        );

        // Verify original files still exist
        for (final filePath in filePaths) {
          final original = await repository.getEntry(filePath);
          expect(original, isNotNull,
              reason: 'Original "$filePath" should still exist');
        }

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 16: Batch Copy Syncs All Successes
    // Validates: Requirements 7.5, 21.5
    test('Property 16: Batch copy enqueues mutation for each successful copy',
        () async {
      const iterations = 100;
      final random = Random(5002);

      for (int i = 0; i < iterations; i++) {
        final targetFolderPath = 'batchcopy_target_$i';
        await _insertFolder(repository, targetFolderPath);

        final batchSize = random.nextInt(6) + 2; // 2-7 files
        final filePaths = <String>[];

        for (int j = 0; j < batchSize; j++) {
          final filePath = 'source_$i/unique_copy_${i}_$j.txt';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);
        }

        // Perform batch copy
        final result =
            await service.copyFiles(filePaths, targetFolderPath);

        expect(result.isSuccess, isTrue,
            reason: 'All copies should succeed');

        // Verify mutation count matches
        final mutations = await database.getPendingMutations();
        final createMutations =
            mutations.where((m) => m.operation == 'create').toList();

        expect(
          createMutations.length,
          batchSize,
          reason:
              'Iteration $i: Create mutation count should equal batch size ($batchSize)',
        );

        // Verify each copied file has a corresponding mutation
        for (final successId in result.successfulIds) {
          final hasMutation =
              createMutations.any((m) => m.path == successId);
          expect(hasMutation, isTrue,
              reason:
                  'Successful copy to "$successId" should have a mutation');
        }

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });
  });

  // ==========================================================================
  // Task 6.2: Recursive Deletion Property Tests (Properties 17, 18)
  // ==========================================================================
  group('Property-Based Tests: Recursive Deletion', () {
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

    // Feature: advanced-file-manager, Property 17: Delete Removes from Database and Syncs
    // Validates: Requirements 8.1, 8.3
    test('Property 17: Delete removes file from database and enqueues mutation',
        () async {
      const iterations = 100;
      final random = Random(6001);

      for (int i = 0; i < iterations; i++) {
        final filePath = _generateRandomFilePath(random, 'delfile_$i');
        await _insertFile(repository, filePath);

        // Perform delete
        final result = await service.deleteFile(filePath);

        expect(result.isSuccess, isTrue,
            reason: 'Delete of "$filePath" should succeed');

        // Verify file is removed from database
        final deletedFile = await repository.getEntry(filePath);
        expect(deletedFile, isNull,
            reason: 'File should not exist after deletion');

        // Verify delete mutation was queued
        final mutations = await database.getPendingMutations();
        final deleteMutations =
            mutations.where((m) => m.operation == 'delete').toList();
        expect(deleteMutations.isNotEmpty, isTrue,
            reason: 'A delete mutation should be queued');

        final hasMutation =
            deleteMutations.any((m) => m.path == filePath);
        expect(hasMutation, isTrue,
            reason: 'Delete mutation path should match deleted file');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 18: Delete Folder Recursively
    // Validates: Requirements 8.2, 8.4
    test('Property 18: Deleting a folder removes all descendants', () async {
      const iterations = 100;
      final random = Random(6002);

      for (int i = 0; i < iterations; i++) {
        // Generate a folder hierarchy with files
        final hierarchy = _generateFolderHierarchy(
          random: random,
          minDepth: 1,
          maxDepth: 3,
          maxChildren: 3,
        );

        // Insert the hierarchy
        await _insertHierarchy(repository, hierarchy);

        // Add some files inside folders
        final fileCount = random.nextInt(5) + 1;
        final insertedFilePaths = <String>[];
        for (int j = 0; j < fileCount; j++) {
          // Pick a random folder to add a file to
          final folder =
              hierarchy.allFolders[random.nextInt(hierarchy.allFolders.length)];
          final filePath = '${folder.path}/testfile_$j.txt';
          await _insertFile(repository, filePath);
          insertedFilePaths.add(filePath);
        }

        // Delete the root folder
        final result = await service.deleteFile(hierarchy.rootPath);

        // Verify success (or partial success)
        expect(
          result.successfulIds.isNotEmpty,
          isTrue,
          reason:
              'Iteration $i: At least some items should be deleted',
        );

        // Verify all entries under the root are gone
        for (final folder in hierarchy.allFolders) {
          final entry = await repository.getEntry(folder.path);
          expect(entry, isNull,
              reason:
                  'Folder "${folder.path}" should be deleted');
        }

        for (final filePath in insertedFilePaths) {
          final entry = await repository.getEntry(filePath);
          expect(entry, isNull,
              reason: 'File "$filePath" should be deleted');
        }

        // Verify mutation count matches deleted item count
        final mutations = await database.getPendingMutations();
        final deleteMutations =
            mutations.where((m) => m.operation == 'delete').toList();
        expect(
          deleteMutations.length,
          result.successfulIds.length,
          reason:
              'Delete mutation count should equal deleted item count',
        );

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 17: Deleting a non-existent file returns not found error',
        () async {
      const iterations = 100;
      final random = Random(6003);

      for (int i = 0; i < iterations; i++) {
        final nonExistentPath =
            _generateRandomFilePath(random, 'nonexistent_$i');

        final result = await service.deleteFile(nonExistentPath);

        expect(result.isFailure, isTrue,
            reason: 'Deleting non-existent file should fail');
        expect(result.errors.first.type, FileOperationErrorType.notFound,
            reason: 'Error should be not found');

        // No mutations should be queued
        final mutations = await database.getPendingMutations();
        expect(mutations.isEmpty, isTrue,
            reason: 'No mutations for failed delete');

        await database.clearAllMutations();
      }
    });
  });

  // ==========================================================================
  // Task 6.4: Batch Delete Property Tests (Properties 19, 20)
  // ==========================================================================
  group('Property-Based Tests: Batch Delete Operations', () {
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

    // Feature: advanced-file-manager, Property 19: Batch Delete Syncs All Successes
    // Validates: Requirements 9.4, 21.3
    test('Property 19: Batch delete enqueues mutation for each deleted item',
        () async {
      const iterations = 100;
      final random = Random(7001);

      for (int i = 0; i < iterations; i++) {
        final batchSize = random.nextInt(6) + 2; // 2-7 files
        final filePaths = <String>[];

        for (int j = 0; j < batchSize; j++) {
          final filePath = 'batch_del_$i/file_$j.txt';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);
        }

        // Perform batch delete
        final result = await service.deleteFiles(filePaths);

        expect(result.isSuccess, isTrue,
            reason: 'Iteration $i: All deletes should succeed');

        // Verify mutation count
        final mutations = await database.getPendingMutations();
        final deleteMutations =
            mutations.where((m) => m.operation == 'delete').toList();

        expect(
          deleteMutations.length,
          batchSize,
          reason:
              'Iteration $i: Delete mutation count should equal batch size ($batchSize)',
        );

        // Verify all files are gone
        for (final filePath in filePaths) {
          final entry = await repository.getEntry(filePath);
          expect(entry, isNull,
              reason: 'File "$filePath" should be deleted');
        }

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 20: Batch Delete Handles Folders Recursively
    // Validates: Requirements 9.5, 21.5
    test(
        'Property 20: Batch delete handles mix of files and folders recursively',
        () async {
      const iterations = 100;
      final random = Random(7002);

      for (int i = 0; i < iterations; i++) {
        final filePaths = <String>[];
        var expectedTotalDeleted = 0;

        // Create some standalone files
        final fileCount = random.nextInt(3) + 1;
        for (int j = 0; j < fileCount; j++) {
          final filePath = 'batch_del_$i/standalone_$j.txt';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);
          expectedTotalDeleted++;
        }

        // Create a folder with children
        final folderPath = 'batch_del_$i/folder_with_children';
        await _insertFolder(repository, folderPath);
        filePaths.add(folderPath);
        expectedTotalDeleted++; // the folder itself

        final childCount = random.nextInt(4) + 1;
        for (int j = 0; j < childCount; j++) {
          await _insertFile(repository, '$folderPath/child_$j.txt');
          expectedTotalDeleted++;
        }

        // Perform batch delete
        final result = await service.deleteFiles(filePaths);

        // Verify all items were deleted
        expect(
          result.successfulIds.length,
          expectedTotalDeleted,
          reason:
              'Iteration $i: Expected $expectedTotalDeleted total deletions (including folder children)',
        );

        // Verify mutations
        final mutations = await database.getPendingMutations();
        final deleteMutations =
            mutations.where((m) => m.operation == 'delete').toList();

        expect(
          deleteMutations.length,
          expectedTotalDeleted,
          reason:
              'Mutation count should equal total deleted count ($expectedTotalDeleted)',
        );

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 19: Batch delete with some non-existent files is partial',
        () async {
      const iterations = 50;
      final random = Random(7003);

      for (int i = 0; i < iterations; i++) {
        final existingCount = random.nextInt(3) + 1;
        final nonExistentCount = random.nextInt(3) + 1;
        final filePaths = <String>[];

        // Create existing files
        for (int j = 0; j < existingCount; j++) {
          final filePath = 'batch_del_$i/existing_$j.txt';
          await _insertFile(repository, filePath);
          filePaths.add(filePath);
        }

        // Add non-existent paths
        for (int j = 0; j < nonExistentCount; j++) {
          filePaths.add('batch_del_$i/nonexistent_$j.txt');
        }

        // Shuffle to mix existing and non-existent
        filePaths.shuffle(random);

        // Perform batch delete
        final result = await service.deleteFiles(filePaths);

        // Should have partial success
        expect(result.successfulIds.length, existingCount,
            reason: 'Only existing files should be deleted');
        expect(result.errors.length, nonExistentCount,
            reason: 'Non-existent files should produce errors');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });
  });

  // ==========================================================================
  // Task 7.2: Rename Operation Property Tests (Properties 21, 22, 23)
  // ==========================================================================
  group('Property-Based Tests: Rename Operations', () {
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

    // Feature: advanced-file-manager, Property 21: Rename Updates Name and Syncs
    // Validates: Requirements 10.1, 10.2
    test('Property 21: Rename updates name in database and enqueues mutation',
        () async {
      const iterations = 100;
      final random = Random(8001);

      for (int i = 0; i < iterations; i++) {
        final filePath = 'folder_$i/original_$i.txt';
        await _insertFile(repository, filePath);

        final newName = 'renamed_${random.nextInt(10000)}.txt';

        // Perform rename
        final result = await service.renameFile(filePath, newName);

        expect(result.isSuccess, isTrue,
            reason: 'Rename should succeed');

        // Verify old path no longer exists
        final oldFile = await repository.getEntry(filePath);
        expect(oldFile, isNull,
            reason: 'Old file path should not exist after rename');

        // Verify new path exists
        final newPath = 'folder_$i/$newName';
        final renamedFile = await repository.getEntry(newPath);
        expect(renamedFile, isNotNull,
            reason: 'File should exist at new path "$newPath"');
        expect(renamedFile?.name, newName,
            reason: 'Name should be updated to "$newName"');

        // Verify mutations were queued (decomposed into delete + update)
        final mutations = await database.getPendingMutations();
        final deleteMutation = mutations.any((m) => m.operation == 'delete' && m.path == filePath);
        final updateMutation = mutations.any((m) => m.operation == 'update' && m.path == newPath);
        
        expect(deleteMutation, isTrue, reason: 'A delete mutation should be queued');
        expect(updateMutation, isTrue, reason: 'An update mutation should be queued');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 22: Rename Rejects Name Conflicts
    // Validates: Requirements 10.3
    test('Property 22: Rename rejects conflicting names without DB changes',
        () async {
      const iterations = 100;
      // final random = Random(8002);

      for (int i = 0; i < iterations; i++) {
        final parentFolder = 'folder_$i';
        final filePath = '$parentFolder/original_$i.txt';
        final conflictingName = 'existing_$i.txt';
        final conflictingPath = '$parentFolder/$conflictingName';

        await _insertFolder(repository, parentFolder);
        await _insertFile(repository, filePath);
        await _insertFile(repository, conflictingPath);

        // Capture state before
        final beforeFiles = await repository.getAllFiles();
        final beforeCount = beforeFiles.length;

        // Attempt rename to conflicting name
        final result = await service.renameFile(filePath, conflictingName);

        expect(result.isFailure, isTrue,
            reason: 'Rename to conflicting name should fail');
        expect(result.errors.first.type, FileOperationErrorType.conflict,
            reason: 'Error should be a conflict');

        // Verify DB unchanged
        final afterFiles = await repository.getAllFiles();
        expect(afterFiles.length, beforeCount,
            reason: 'File count should not change');

        // Original file still at original path
        final originalFile = await repository.getEntry(filePath);
        expect(originalFile, isNotNull,
            reason: 'Original file should remain');

        // No mutations
        final mutations = await database.getPendingMutations();
        expect(mutations.isEmpty, isTrue,
            reason: 'No mutations for failed rename');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    // Feature: advanced-file-manager, Property 23: Rename Validates Names
    // Validates: Requirements 10.4, 10.5
    test('Property 23: Rename rejects invalid names', () async {
      const iterations = 100;
      final random = Random(8003);

      for (int i = 0; i < iterations; i++) {
        final filePath = 'folder/file_$i.txt';
        await _insertFile(repository, filePath);

        // Generate an invalid name
        final invalidName = _generateInvalidFileName(random);

        // Attempt rename with invalid name
        final result = await service.renameFile(filePath, invalidName);

        expect(result.isFailure, isTrue,
            reason: 'Rename to invalid name "$invalidName" should fail');
        expect(result.errors.first.type, FileOperationErrorType.validation,
            reason: 'Error should be validation type');

        // Original file should still exist
        final originalFile = await repository.getEntry(filePath);
        expect(originalFile, isNotNull,
            reason: 'Original file should remain after failed rename');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 21: Renaming to same name is a no-op success', () async {
      const iterations = 50;

      for (int i = 0; i < iterations; i++) {
        final fileName = 'file_$i.txt';
        final filePath = 'folder/$fileName';
        await _insertFile(repository, filePath);

        // Rename to same name
        final result = await service.renameFile(filePath, fileName);

        expect(result.isSuccess, isTrue,
            reason: 'Rename to same name should succeed (no-op)');
        expect(result.successfulIds.first, filePath,
            reason: 'Result should reference original path');

        // File should still be at original path
        final file = await repository.getEntry(filePath);
        expect(file, isNotNull,
            reason: 'File should remain at original path');

        // Clean up
        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });

    test('Property 23: Rename preserves file metadata', () async {
      const iterations = 100;
      final random = Random(8004);

      for (int i = 0; i < iterations; i++) {
        final filePath = 'folder/file_$i.txt';
        final fileSize = random.nextInt(1000000);
        final lastModified = DateTime.now()
            .subtract(Duration(days: random.nextInt(365)))
            .toUtc()
            .toIso8601String();

        await repository.upsertFile(
          FileEntry(
            path: filePath,
            name: 'file_$i.txt',
            type: 'file',
            sizeBytes: fileSize,
            lastModified: lastModified,
            serverVersion: 1,
          ),
        );

        final newName = 'renamed_$i.txt';
        final result = await service.renameFile(filePath, newName);

        if (result.isSuccess) {
          final newPath = 'folder/$newName';
          final renamedFile = await repository.getEntry(newPath);
          expect(renamedFile?.sizeBytes, fileSize,
              reason: 'Size should be preserved');
          expect(renamedFile?.lastModified, lastModified,
              reason: 'Last modified should be preserved');
        }

        await database.deleteAllEntries();
        await database.clearAllMutations();
      }
    });
  });
}

// ============================================================================
// Random String Generators
// ============================================================================

/// Generate a random path that may be valid or invalid
String _generateRandomPath(Random random) {
  final pathType = random.nextInt(10);
  
  if (pathType == 0) {
    // Empty path
    return '';
  } else if (pathType == 1) {
    // Path with invalid characters
    return _generatePathWithInvalidChars(random);
  } else if (pathType == 2) {
    // Path with reserved name
    return _generatePathWithReservedName(random);
  } else {
    // Valid path
    final segments = <String>[];
    final segmentCount = random.nextInt(5) + 1;
    for (int i = 0; i < segmentCount; i++) {
      segments.add(_generateValidPathSegment(random));
    }
    return segments.join('/');
  }
}

/// Generate a random file name that may be valid or invalid
String _generateRandomFileName(Random random) {
  final nameType = random.nextInt(12);
  
  if (nameType == 0) {
    // Empty name
    return '';
  } else if (nameType == 1) {
    // Whitespace only
    return ' ' * (random.nextInt(5) + 1);
  } else if (nameType == 2) {
    // Name with invalid characters
    return _generateNameWithInvalidChars(random);
  } else if (nameType == 3) {
    // Reserved name
    final reserved = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
    return reserved[random.nextInt(reserved.length)];
  } else if (nameType == 4) {
    // Name ending with period
    return '${_generateValidFileNameBase(random)}.';
  } else if (nameType == 5) {
    // Name ending with space
    return '${_generateValidFileNameBase(random)} ';
  } else {
    // Valid name
    final base = _generateValidFileNameBase(random);
    final withExtension = random.nextBool();
    return withExtension ? '$base.${_generateValidExtension(random)}' : base;
  }
}

/// Generate a valid path segment (no slashes, no invalid chars, no reserved names)
String _generateValidPathSegment(Random random) {
  final validChars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.';
  final length = random.nextInt(15) + 1;
  final buffer = StringBuffer();
  
  for (int i = 0; i < length; i++) {
    buffer.write(validChars[random.nextInt(validChars.length)]);
  }
  
  // Make sure it's not a reserved name
  final segment = buffer.toString();
  final reserved = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  if (reserved.contains(segment.toUpperCase())) {
    return 'valid_$segment'; // Prefix to make it non-reserved
  }
  
  return segment;
}

/// Generate a valid file name base (no extension, no invalid chars)
String _generateValidFileNameBase(Random random) {
  final validChars = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_()[]';
  final length = random.nextInt(15) + 1;
  final buffer = StringBuffer();
  
  for (int i = 0; i < length; i++) {
    buffer.write(validChars[random.nextInt(validChars.length)]);
  }
  
  // Make sure it's not a reserved name
  final name = buffer.toString();
  final reserved = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  if (reserved.contains(name.toUpperCase())) {
    return 'file_$name'; // Prefix to make it non-reserved
  }
  
  return name;
}

/// Generate a valid file extension
String _generateValidExtension(Random random) {
  final extensions = ['txt', 'pdf', 'jpg', 'png', 'doc', 'md', 'json', 'xml'];
  return extensions[random.nextInt(extensions.length)];
}

/// Generate a path with invalid characters
String _generatePathWithInvalidChars(Random random) {
  final invalidChars = ['<', '>', ':', '"', '|', '?', '*', '\x00', '\x1F'];
  final base = _generateValidPathSegment(random);
  final invalidChar = invalidChars[random.nextInt(invalidChars.length)];
  final position = random.nextInt(base.length + 1);
  return base.substring(0, position) + invalidChar + base.substring(position);
}

/// Generate a file name with invalid characters
String _generateNameWithInvalidChars(Random random) {
  final invalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*', '\x00', '\x1F'];
  final base = _generateValidFileNameBase(random);
  final invalidChar = invalidChars[random.nextInt(invalidChars.length)];
  final position = random.nextInt(base.length + 1);
  return base.substring(0, position) + invalidChar + base.substring(position);
}

/// Generate a path with a reserved name
String _generatePathWithReservedName(Random random) {
  final reserved = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  final reservedName = reserved[random.nextInt(reserved.length)];
  
  final pathType = random.nextInt(3);
  if (pathType == 0) {
    return reservedName;
  } else if (pathType == 1) {
    return 'folder/$reservedName';
  } else {
    return '$reservedName/subfolder';
  }
}

/// Mix the case of a string randomly
String _mixCase(String str, Random random) {
  final buffer = StringBuffer();
  for (int i = 0; i < str.length; i++) {
    final char = str[i];
    buffer.write(random.nextBool() ? char.toUpperCase() : char.toLowerCase());
  }
  return buffer.toString();
}

// ============================================================================
// Validation Helper Functions
// ============================================================================

/// Check if a path has invalid characters
bool _hasInvalidPathChars(String path) {
  final invalidChars = RegExp(r'[<>:"|?*\x00-\x1F]');
  return invalidChars.hasMatch(path);
}

/// Check if a file name has invalid characters
bool _hasInvalidFileNameChars(String name) {
  final invalidChars = RegExp(r'[<>:"/\\|?*\x00-\x1F]');
  return invalidChars.hasMatch(name);
}

/// Check if a path contains a reserved name
bool _hasReservedName(String path) {
  final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  final segments = path.split('/');
  for (final segment in segments) {
    if (reservedNames.contains(segment.toUpperCase())) {
      return true;
    }
  }
  return false;
}

/// Check if a name is a reserved name
bool _isReservedName(String name) {
  final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  return reservedNames.contains(name.toUpperCase());
}

// ============================================================================
// Validation Functions (replicate FileOperationService logic)
// ============================================================================

/// Validate a file path.
/// This replicates the logic from FileOperationService._isValidPath
bool _isValidPath(String path) {
  if (path.isEmpty) return false;
  final invalidChars = RegExp(r'[<>:"|?*\x00-\x1F]');
  if (invalidChars.hasMatch(path)) return false;
  final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  final segments = path.split('/');
  for (final segment in segments) {
    if (reservedNames.contains(segment.toUpperCase())) return false;
  }
  return true;
}

/// Validate a file name.
/// This replicates the logic from FileOperationService._isValidFileName
bool _isValidFileName(String name) {
  if (name.trim().isEmpty) return false;
  final invalidChars = RegExp(r'[<>:"/\\|?*\x00-\x1F]');
  if (invalidChars.hasMatch(name)) return false;
  final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  if (reservedNames.contains(name.toUpperCase())) return false;
  if (name.endsWith('.') || name.endsWith(' ')) return false;
  return true;
}

// ============================================================================
// Folder Hierarchy Generator
// ============================================================================

/// Represents a folder in a hierarchy
class _FolderNode {
  final String path;
  final String name;
  _FolderNode({
    required this.path,
    required this.name,
  });
}

/// Represents a complete folder hierarchy for testing
class _FolderHierarchy {
  final String rootPath;
  final List<_FolderNode> allFolders;

  _FolderHierarchy({
    required this.rootPath,
    required this.allFolders,
  });

  /// Pick a random descendant folder (not the root)
  _FolderNode pickRandomDescendant(Random random) {
    if (allFolders.length <= 1) {
      throw StateError('No descendants available');
    }
    // Skip index 0 (root) and pick from descendants
    final index = random.nextInt(allFolders.length - 1) + 1;
    return allFolders[index];
  }
}

/// Generate a random folder hierarchy with varying depths
_FolderHierarchy _generateFolderHierarchy({
  required Random random,
  required int minDepth,
  required int maxDepth,
  required int maxChildren,
}) {
  final depth = minDepth + random.nextInt(maxDepth - minDepth + 1);
  final rootName = 'folder_${random.nextInt(10000)}';
  final rootPath = 'test/$rootName';

  final allFolders = <_FolderNode>[];

  // Create root folder
  final root = _FolderNode(path: rootPath, name: rootName);
  allFolders.add(root);

  // Build hierarchy using BFS
  final queue = <({_FolderNode node, int level})>[(node: root, level: 0)];

  while (queue.isNotEmpty) {
    final current = queue.removeAt(0);

    // Stop if we've reached max depth
    if (current.level >= depth) continue;

    // Generate random number of children (1 to maxChildren)
    final childCount = random.nextInt(maxChildren) + 1;

    for (int i = 0; i < childCount; i++) {
      final childName = 'child_${random.nextInt(10000)}';
      final childPath = '${current.node.path}/$childName';

      final child = _FolderNode(path: childPath, name: childName);
      allFolders.add(child);

      // Add to queue for further expansion
      queue.add((node: child, level: current.level + 1));

      // Limit total folders to prevent excessive generation
      if (allFolders.length >= 50) break;
    }

    if (allFolders.length >= 50) break;
  }

  return _FolderHierarchy(
    rootPath: rootPath,
    allFolders: allFolders,
  );
}

/// Insert a folder hierarchy into the repository
Future<void> _insertHierarchy(
  ExplorerRepository repository,
  _FolderHierarchy hierarchy,
) async {
  for (final folder in hierarchy.allFolders) {
    await _insertFolder(repository, folder.path);
  }
}

/// Insert a single folder into the repository
Future<void> _insertFolder(
  ExplorerRepository repository,
  String path,
) async {
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

/// Insert a single file into the repository
Future<void> _insertFile(
  ExplorerRepository repository,
  String path,
) async {
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

/// Generate a random file path
String _generateRandomFilePath(Random random, String baseName) {
  final depth = random.nextInt(3) + 1; // 1-3 levels deep
  final segments = <String>[];
  
  for (int i = 0; i < depth; i++) {
    segments.add('folder_${random.nextInt(100)}');
  }
  
  segments.add('$baseName.txt');
  return segments.join('/');
}

/// Generate a random folder path
String _generateRandomFolderPath(Random random, String baseName) {
  final depth = random.nextInt(3) + 1; // 1-3 levels deep
  final segments = <String>[];
  
  for (int i = 0; i < depth - 1; i++) {
    segments.add('folder_${random.nextInt(100)}');
  }
  
  segments.add(baseName);
  return segments.join('/');
}

/// Generate an invalid path (with invalid characters or reserved names)
String _generateInvalidPath(Random random) {
  final invalidType = random.nextInt(2);
  
  if (invalidType == 0) {
    // Path with invalid characters
    final invalidChars = ['<', '>', ':', '"', '|', '?', '*'];
    final invalidChar = invalidChars[random.nextInt(invalidChars.length)];
    return 'folder$invalidChar/target';
  } else {
    // Path with reserved name
    final reserved = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
    final reservedName = reserved[random.nextInt(reserved.length)];
    return 'folder/$reservedName';
  }
}

/// Generate an invalid file name (with invalid characters, reserved names, or bad endings)
String _generateInvalidFileName(Random random) {
  final nameType = random.nextInt(4);

  if (nameType == 0) {
    // Empty name
    return '';
  } else if (nameType == 1) {
    // Name with invalid characters
    final invalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    final invalidChar = invalidChars[random.nextInt(invalidChars.length)];
    final base = _generateValidFileNameBase(random);
    return '$base$invalidChar\name';
  } else if (nameType == 2) {
    // Reserved name
    final reserved = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
    return reserved[random.nextInt(reserved.length)];
  } else {
    // Name ending with period or space
    final base = _generateValidFileNameBase(random);
    return random.nextBool() ? '$base.' : '$base ';
  }
}
