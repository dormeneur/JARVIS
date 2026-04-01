import 'package:flutter_test/flutter_test.dart';
import 'package:drift/native.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/features/explorer/domain/services/file_operation_service.dart';
import 'package:jarvis_mobile/features/explorer/domain/models/file_operation_result.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

void main() {
  group('Path Validation', () {
    test('_isValidPath returns false for empty path', () {
      final result = _isValidPath('');
      expect(result, isFalse);
    });

    test('_isValidPath returns false for path with invalid characters', () {
      final invalidPaths = [
        'folder<name',
        'folder>name',
        'folder:name',
        'folder"name',
        'folder|name',
        'folder?name',
        'folder*name',
        'folder\x00name',
        'folder\x1Fname',
      ];

      for (final path in invalidPaths) {
        final result = _isValidPath(path);
        expect(result, isFalse, reason: 'Path "$path" should be invalid');
      }
    });

    test('_isValidPath returns false for path with reserved names', () {
      final reservedPaths = [
        'CON',
        'PRN',
        'AUX',
        'NUL',
        'COM1',
        'LPT1',
        'folder/CON/file',
        'folder/prn/file', // Case insensitive
        'folder/Aux/file',
      ];

      for (final path in reservedPaths) {
        final result = _isValidPath(path);
        expect(result, isFalse, reason: 'Path "$path" should be invalid');
      }
    });

    test('_isValidPath returns true for valid paths', () {
      final validPaths = [
        'folder',
        'folder/subfolder',
        'folder/subfolder/file.txt',
        'my-folder',
        'my_folder',
        'folder123',
        'folder.name',
        'ACON', // Not a reserved name
        'COM2', // Not in reserved list
      ];

      for (final path in validPaths) {
        final result = _isValidPath(path);
        expect(result, isTrue, reason: 'Path "$path" should be valid');
      }
    });
  });

  group('File Name Validation', () {
    test('_isValidFileName returns false for empty name', () {
      final result = _isValidFileName('');
      expect(result, isFalse);
    });

    test('_isValidFileName returns false for whitespace-only name', () {
      final result = _isValidFileName('   ');
      expect(result, isFalse);
    });

    test('_isValidFileName returns false for name with invalid characters', () {
      final invalidNames = [
        'file<name',
        'file>name',
        'file:name',
        'file"name',
        'file/name',
        'file\\name',
        'file|name',
        'file?name',
        'file*name',
        'file\x00name',
        'file\x1Fname',
      ];

      for (final name in invalidNames) {
        final result = _isValidFileName(name);
        expect(result, isFalse, reason: 'Name "$name" should be invalid');
      }
    });

    test('_isValidFileName returns false for reserved names', () {
      final reservedNames = [
        'CON',
        'PRN',
        'AUX',
        'NUL',
        'COM1',
        'LPT1',
        'con', // Case insensitive
        'Prn',
        'AuX',
      ];

      for (final name in reservedNames) {
        final result = _isValidFileName(name);
        expect(result, isFalse, reason: 'Name "$name" should be invalid');
      }
    });

    test('_isValidFileName returns false for names ending with period', () {
      final invalidNames = [
        'file.',
        'file.txt.',
        'filename.',
      ];

      for (final name in invalidNames) {
        final result = _isValidFileName(name);
        expect(result, isFalse, reason: 'Name "$name" should be invalid');
      }
    });

    test('_isValidFileName returns false for names ending with space', () {
      final invalidNames = [
        'file ',
        'file.txt ',
        'filename ',
      ];

      for (final name in invalidNames) {
        final result = _isValidFileName(name);
        expect(result, isFalse, reason: 'Name "$name" should be invalid');
      }
    });

    test('_isValidFileName returns true for valid names', () {
      final validNames = [
        'file',
        'file.txt',
        'my-file',
        'my_file',
        'file123',
        'file.name.txt',
        'ACON', // Not a reserved name
        'COM2', // Not in reserved list
        'file (1)',
        'file [copy]',
      ];

      for (final name in validNames) {
        final result = _isValidFileName(name);
        expect(result, isTrue, reason: 'Name "$name" should be valid');
      }
    });
  });

  group('Circular Move Detection', () {
    late AppDatabase db;
    late ExplorerRepository repository;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
    });

    tearDown(() async {
      await db.close();
    });

    test('getChildrenPaths returns all descendants in folder hierarchy', () async {
      // Setup: Create deep folder hierarchy
      await repository.upsertFile(
        const FileEntry(
          path: 'root',
          name: 'root',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'root/level1',
          name: 'level1',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'root/level1/level2',
          name: 'level2',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'root/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Get all children of root
      final children = await repository.getChildrenPaths('root');

      expect(children, hasLength(2));
      expect(children, contains('root/level1'));
      expect(children, contains('root/file.txt'));
      expect(children, isNot(contains('root/level1/level2')));
    });

    test('getFolderPaths correctly filters folders from mixed list', () async {
      // Setup: Create mixed files and folders
      await repository.upsertFile(
        const FileEntry(
          path: 'folder1',
          name: 'folder1',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder2',
          name: 'folder2',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Filter to only folders
      final allPaths = ['folder1', 'file1.txt', 'folder2'];
      final folders = await repository.getFolderPaths(allPaths);

      expect(folders, hasLength(2));
      expect(folders, contains('folder1'));
      expect(folders, contains('folder2'));
      expect(folders, isNot(contains('file1.txt')));
    });
  });

  group('Helper Methods', () {
    late AppDatabase db;
    late ExplorerRepository repository;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
    });

    tearDown(() async {
      await db.close();
    });

    test('getChildrenPaths returns direct children only', () async {
      // Setup: Create folder hierarchy
      await repository.upsertFile(
        const FileEntry(
          path: 'parent',
          name: 'parent',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child1',
          name: 'child1',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child2',
          name: 'child2',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child2/grandchild',
          name: 'grandchild',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Get children of parent
      final children = await repository.getChildrenPaths('parent');

      expect(children, hasLength(2));
      expect(children, contains('parent/child1'));
      expect(children, contains('parent/child2'));
      expect(children, isNot(contains('parent/child2/grandchild')));
    });

    test('getFolderPaths filters only folders', () async {
      // Setup: Create mixed files and folders
      await repository.upsertFile(
        const FileEntry(
          path: 'folder1',
          name: 'folder1',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder2',
          name: 'folder2',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Filter to only folders
      final allPaths = ['folder1', 'file1.txt', 'folder2', 'file2.txt'];
      final folders = await repository.getFolderPaths(allPaths);

      expect(folders, hasLength(2));
      expect(folders, contains('folder1'));
      expect(folders, contains('folder2'));
      expect(folders, isNot(contains('file1.txt')));
      expect(folders, isNot(contains('file2.txt')));
    });

    test('getFileNamesInPath returns all file names in a folder', () async {
      // Setup: Create folder with files
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/subfolder',
          name: 'subfolder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/subfolder/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Get file names in folder
      final names = await repository.getFileNamesInPath('folder');

      expect(names, hasLength(3));
      expect(names, contains('file1.txt'));
      expect(names, contains('file2.txt'));
      expect(names, contains('subfolder'));
      expect(names, isNot(contains('file3.txt'))); // Not a direct child
    });

    test('getFileNamesInPath returns empty set for empty folder', () async {
      // Setup: Create empty folder
      await repository.upsertFile(
        const FileEntry(
          path: 'empty',
          name: 'empty',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Get file names in empty folder
      final names = await repository.getFileNamesInPath('empty');

      expect(names, isEmpty);
    });

    test('getFileNamesInPath works with root path', () async {
      // Setup: Create files in root
      await repository.upsertFile(
        const FileEntry(
          path: 'file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Get file names in root (empty path)
      final names = await repository.getFileNamesInPath('');

      expect(names, hasLength(2));
      expect(names, contains('file1.txt'));
      expect(names, contains('file2.txt'));
      expect(names, isNot(contains('file3.txt'))); // Not in root
    });
  });

  group('Unique Name Generation', () {
    late AppDatabase db;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
      service = FileOperationService(
        repository: repository,
        database: db,
      );
    });

    tearDown(() async {
      await db.close();
    });

    test('generates unique name with (1) suffix for first conflict', () async {
      // Setup: Create folder with existing file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Generate unique name
      final uniqueName = await service.generateUniqueName('file.txt', 'folder');

      expect(uniqueName, 'file (1).txt');
    });

    test('generates unique name with (2) suffix when (1) exists', () async {
      // Setup: Create folder with existing files
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file (1).txt',
          name: 'file (1).txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Generate unique name
      final uniqueName = await service.generateUniqueName('file.txt', 'folder');

      expect(uniqueName, 'file (2).txt');
    });

    test('generates unique name with high counter when many conflicts exist', () async {
      // Setup: Create folder with many existing files
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      for (int i = 1; i <= 10; i++) {
        await repository.upsertFile(
          FileEntry(
            path: 'folder/file ($i).txt',
            name: 'file ($i).txt',
            type: 'file',
            lastModified: '2024-01-01T00:00:00Z',
          ),
        );
      }

      // Test: Generate unique name
      final uniqueName = await service.generateUniqueName('file.txt', 'folder');

      expect(uniqueName, 'file (11).txt');
    });

    test('returns original name when no conflict exists', () async {
      // Setup: Create empty folder
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Generate unique name
      final uniqueName = await service.generateUniqueName('file.txt', 'folder');

      expect(uniqueName, 'file.txt');
    });

    test('handles files without extensions correctly', () async {
      // Setup: Create folder with existing file without extension
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/README',
          name: 'README',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Generate unique name
      final uniqueName = await service.generateUniqueName('README', 'folder');

      expect(uniqueName, 'README (1)');
    });

    test('handles files with multiple dots correctly', () async {
      // Setup: Create folder with existing file with multiple dots
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/archive.tar.gz',
          name: 'archive.tar.gz',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Generate unique name
      final uniqueName = await service.generateUniqueName('archive.tar.gz', 'folder');

      expect(uniqueName, 'archive.tar (1).gz');
    });

    test('handles folder names correctly', () async {
      // Setup: Create folder with existing subfolder
      await repository.upsertFile(
        const FileEntry(
          path: 'parent',
          name: 'parent',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/subfolder',
          name: 'subfolder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Generate unique name
      final uniqueName = await service.generateUniqueName('subfolder', 'parent');

      expect(uniqueName, 'subfolder (1)');
    });

    test('works with root path (empty string)', () async {
      // Setup: Create file in root
      await repository.upsertFile(
        const FileEntry(
          path: 'file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Test: Generate unique name in root
      final uniqueName = await service.generateUniqueName('file.txt', '');

      expect(uniqueName, 'file (1).txt');
    });
  });

  group('moveFile Operation', () {
    late AppDatabase db;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
      service = FileOperationService(
        repository: repository,
        database: db,
      );
    });

    tearDown(() async {
      await db.close();
    });

    test('moveFile successfully moves a file to target folder', () async {
      // Setup: Create source file and target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Move file to target folder
      final result = await service.moveFile('source/file.txt', 'target');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'target/file.txt');
      expect(result.errors, isEmpty);

      // Verify: Old path no longer exists
      final oldFile = await repository.getEntry('source/file.txt');
      expect(oldFile, isNull);

      // Verify: New path exists
      final newFile = await repository.getEntry('target/file.txt');
      expect(newFile, isNotNull);
      expect(newFile!.name, 'file.txt');
      expect(newFile.path, 'target/file.txt');

      // Verify: Mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.first.operation, 'delete');
      expect(['target/file.txt', 'source/file.txt'].contains(mutations.first.path), isTrue);
    });

    test('moveFile returns error for non-existent source file', () async {
      // Setup: Create target folder only
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move non-existent file
      final result = await service.moveFile('nonexistent.txt', 'target');

      // Verify: Operation failed with notFound error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.notFound);
      expect(result.errors.first.message, 'File not found');

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('moveFile returns error for non-existent target folder', () async {
      // Setup: Create source file only
      await repository.upsertFile(
        const FileEntry(
          path: 'file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move to non-existent folder
      final result = await service.moveFile('file.txt', 'nonexistent');

      // Verify: Operation failed with notFound error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.notFound);
      expect(result.errors.first.message, 'Target folder not found');

      // Verify: File still exists at original path
      final file = await repository.getEntry('file.txt');
      expect(file, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('moveFile returns error for name conflict in target folder', () async {
      // Setup: Create source file, target folder, and conflicting file
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move file to folder with same name
      final result = await service.moveFile('source/file.txt', 'target');

      // Verify: Operation failed with conflict error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.conflict);
      expect(result.errors.first.message, contains('already exists'));

      // Verify: Source file still exists
      final sourceFile = await repository.getEntry('source/file.txt');
      expect(sourceFile, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('moveFile returns error for circular move (folder into itself)', () async {
      // Setup: Create folder
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move folder into itself
      final result = await service.moveFile('folder', 'folder');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);
      expect(result.errors.first.message, contains('Cannot move a folder into itself'));

      // Verify: Folder still exists
      final folder = await repository.getEntry('folder');
      expect(folder, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('moveFile returns error for circular move (folder into descendant)', () async {
      // Setup: Create folder hierarchy
      await repository.upsertFile(
        const FileEntry(
          path: 'parent',
          name: 'parent',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child',
          name: 'child',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move parent into child
      final result = await service.moveFile('parent', 'parent/child');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);
      expect(result.errors.first.message, contains('Cannot move a folder into itself'));

      // Verify: Folder still exists at original path
      final folder = await repository.getEntry('parent');
      expect(folder, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('moveFile returns error for invalid source path', () async {
      // Execute: Try to move with invalid path
      final result = await service.moveFile('invalid<path', 'target');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);
      expect(result.errors.first.message, 'Invalid source file path');
    });

    test('moveFile returns error for invalid target path', () async {
      // Setup: Create source file
      await repository.upsertFile(
        const FileEntry(
          path: 'file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move to invalid path
      final result = await service.moveFile('file.txt', 'invalid|path');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);
      expect(result.errors.first.message, 'Invalid target folder path');
    });

    test('moveFile returns error when target is a file, not a folder', () async {
      // Setup: Create source file and target file (not folder)
      await repository.upsertFile(
        const FileEntry(
          path: 'source.txt',
          name: 'source.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target.txt',
          name: 'target.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move to a file
      final result = await service.moveFile('source.txt', 'target.txt');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);
      expect(result.errors.first.message, 'Target must be a folder');
    });

    test('moveFile updates all descendant paths when moving a folder', () async {
      // Setup: Create folder hierarchy
      // parent/
      //   child/
      //     file1.txt
      //     file2.txt
      //   file3.txt
      await repository.upsertFile(
        const FileEntry(
          path: 'parent',
          name: 'parent',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child',
          name: 'child',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      
      // Create target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Move parent folder to target
      final result = await service.moveFile('parent', 'target');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'target/parent');

      // Verify: Parent folder moved
      final movedParent = await repository.getEntry('target/parent');
      expect(movedParent, isNotNull);
      expect(movedParent!.name, 'parent');
      expect(movedParent.type, 'directory');

      // Verify: Old parent folder no longer exists
      final oldParent = await repository.getEntry('parent');
      expect(oldParent, isNull);

      // Verify: All descendants have updated paths
      final movedChild = await repository.getEntry('target/parent/child');
      expect(movedChild, isNotNull);
      expect(movedChild!.name, 'child');

      final movedFile1 = await repository.getEntry('target/parent/child/file1.txt');
      expect(movedFile1, isNotNull);
      expect(movedFile1!.name, 'file1.txt');

      final movedFile2 = await repository.getEntry('target/parent/child/file2.txt');
      expect(movedFile2, isNotNull);
      expect(movedFile2!.name, 'file2.txt');

      final movedFile3 = await repository.getEntry('target/parent/file3.txt');
      expect(movedFile3, isNotNull);
      expect(movedFile3!.name, 'file3.txt');

      // Verify: Old descendant paths no longer exist
      expect(await repository.getEntry('parent/child'), isNull);
      expect(await repository.getEntry('parent/child/file1.txt'), isNull);
      expect(await repository.getEntry('parent/child/file2.txt'), isNull);
      expect(await repository.getEntry('parent/file3.txt'), isNull);

      // Verify: Mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.first.operation, 'delete');
      expect(mutations.first.path, 'target/parent');
    });
  });

  group('moveFiles Batch Operation', () {
    late AppDatabase db;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
      service = FileOperationService(
        repository: repository,
        database: db,
      );
    });

    tearDown(() async {
      await db.close();
    });

    test('moveFiles successfully moves multiple files to target folder', () async {
      // Setup: Create source files and target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Move all files to target folder
      final result = await service.moveFiles(
        ['source/file1.txt', 'source/file2.txt', 'source/file3.txt'],
        'target',
      );

      // Verify: Operation succeeded for all files
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(3));
      expect(result.successfulIds, contains('target/file1.txt'));
      expect(result.successfulIds, contains('target/file2.txt'));
      expect(result.successfulIds, contains('target/file3.txt'));
      expect(result.errors, isEmpty);

      // Verify: All files moved to target
      final movedFile1 = await repository.getEntry('target/file1.txt');
      expect(movedFile1, isNotNull);
      final movedFile2 = await repository.getEntry('target/file2.txt');
      expect(movedFile2, isNotNull);
      final movedFile3 = await repository.getEntry('target/file3.txt');
      expect(movedFile3, isNotNull);

      // Verify: Old paths no longer exist
      expect(await repository.getEntry('source/file1.txt'), isNull);
      expect(await repository.getEntry('source/file2.txt'), isNull);
      expect(await repository.getEntry('source/file3.txt'), isNull);

      // Verify: Mutations were enqueued for all files
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'move'), isTrue);
    });

    test('moveFiles handles partial success with name conflicts', () async {
      // Setup: Create source files, target folder, and one conflicting file
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      // Create conflicting file in target
      await repository.upsertFile(
        const FileEntry(
          path: 'target/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Move all files to target folder
      final result = await service.moveFiles(
        ['source/file1.txt', 'source/file2.txt', 'source/file3.txt'],
        'target',
      );

      // Verify: Partial success
      expect(result.isPartialSuccess, isTrue);
      expect(result.successfulIds, hasLength(2));
      expect(result.successfulIds, contains('target/file1.txt'));
      expect(result.successfulIds, contains('target/file3.txt'));
      expect(result.errors, hasLength(1));
      expect(result.errors.first.fileId, 'source/file2.txt');
      expect(result.errors.first.type, FileOperationErrorType.conflict);

      // Verify: Successful files moved
      expect(await repository.getEntry('target/file1.txt'), isNotNull);
      expect(await repository.getEntry('target/file3.txt'), isNotNull);

      // Verify: Failed file still at source
      expect(await repository.getEntry('source/file2.txt'), isNotNull);

      // Verify: Only successful moves enqueued mutations
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
    });

    test('moveFiles continues processing after individual failures', () async {
      // Setup: Create source files and target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move files including a non-existent one
      final result = await service.moveFiles(
        ['source/file1.txt', 'source/nonexistent.txt', 'source/file3.txt'],
        'target',
      );

      // Verify: Partial success - continues after failure
      expect(result.isPartialSuccess, isTrue);
      expect(result.successfulIds, hasLength(2));
      expect(result.successfulIds, contains('target/file1.txt'));
      expect(result.successfulIds, contains('target/file3.txt'));
      expect(result.errors, hasLength(1));
      expect(result.errors.first.fileId, 'source/nonexistent.txt');
      expect(result.errors.first.type, FileOperationErrorType.notFound);

      // Verify: Successful files moved
      expect(await repository.getEntry('target/file1.txt'), isNotNull);
      expect(await repository.getEntry('target/file3.txt'), isNotNull);

      // Verify: Mutations enqueued for successful moves
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
    });

    test('moveFiles returns all failures when all operations fail', () async {
      // Setup: Create target folder only (no source files)
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to move non-existent files
      final result = await service.moveFiles(
        ['nonexistent1.txt', 'nonexistent2.txt', 'nonexistent3.txt'],
        'target',
      );

      // Verify: Complete failure
      expect(result.isFailure, isTrue);
      expect(result.successfulIds, isEmpty);
      expect(result.errors, hasLength(3));
      expect(result.errors.every((e) => e.type == FileOperationErrorType.notFound), isTrue);

      // Verify: No mutations enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('moveFiles handles empty file list', () async {
      // Setup: Create target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Move empty list
      final result = await service.moveFiles([], 'target');

      // Verify: No operations performed (not success, not failure)
      expect(result.isSuccess, isFalse);
      expect(result.isFailure, isFalse);
      expect(result.isPartialSuccess, isFalse);
      expect(result.successfulIds, isEmpty);
      expect(result.errors, isEmpty);
      expect(result.message, 'No operations performed');

      // Verify: No mutations enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });
  });

  group('deleteFile Operation', () {
    late AppDatabase db;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
      service = FileOperationService(
        repository: repository,
        database: db,
      );
    });

    tearDown(() async {
      await db.close();
    });

    test('deleteFile successfully deletes a single file', () async {
      // Setup: Create a file
      await repository.upsertFile(
        const FileEntry(
          path: 'file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Delete the file
      final result = await service.deleteFile('file.txt');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'file.txt');
      expect(result.errors, isEmpty);

      // Verify: File no longer exists
      final deletedFile = await repository.getEntry('file.txt');
      expect(deletedFile, isNull);

      // Verify: Delete mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.first.operation, 'delete');
      expect(mutations.first.path, 'file.txt');
    });

    test('deleteFile returns error for non-existent file', () async {
      // Execute: Try to delete non-existent file
      final result = await service.deleteFile('nonexistent.txt');

      // Verify: Operation failed with notFound error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.notFound);
      expect(result.errors.first.message, 'File not found');

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('deleteFile recursively deletes folder with children', () async {
      // Setup: Create folder hierarchy
      // parent/
      //   child/
      //     file1.txt
      //     file2.txt
      //   file3.txt
      await repository.upsertFile(
        const FileEntry(
          path: 'parent',
          name: 'parent',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child',
          name: 'child',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/child/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Delete parent folder
      final result = await service.deleteFile('parent');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(5)); // parent + child + 3 files
      expect(result.successfulIds, contains('parent'));
      expect(result.successfulIds, contains('parent/child'));
      expect(result.successfulIds, contains('parent/child/file1.txt'));
      expect(result.successfulIds, contains('parent/child/file2.txt'));
      expect(result.successfulIds, contains('parent/file3.txt'));
      expect(result.errors, isEmpty);

      // Verify: All files and folders no longer exist
      expect(await repository.getEntry('parent'), isNull);
      expect(await repository.getEntry('parent/child'), isNull);
      expect(await repository.getEntry('parent/child/file1.txt'), isNull);
      expect(await repository.getEntry('parent/child/file2.txt'), isNull);
      expect(await repository.getEntry('parent/file3.txt'), isNull);

      // Verify: Delete mutations were enqueued for all items
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'delete'), isTrue);
    });

    test('deleteFile deletes empty folder', () async {
      // Setup: Create empty folder
      await repository.upsertFile(
        const FileEntry(
          path: 'empty',
          name: 'empty',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Delete empty folder
      final result = await service.deleteFile('empty');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'empty');
      expect(result.errors, isEmpty);

      // Verify: Folder no longer exists
      final deletedFolder = await repository.getEntry('empty');
      expect(deletedFolder, isNull);

      // Verify: Delete mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.first.operation, 'delete');
      expect(mutations.first.path, 'empty');
    });

    test('deleteFile deletes children before parent folder', () async {
      // Setup: Create folder with files
      await repository.upsertFile(
        const FileEntry(
          path: 'folder',
          name: 'folder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Delete folder
      final result = await service.deleteFile('folder');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(3)); // folder + 2 files
      
      // Verify: Children are deleted before parent
      // The order in successfulIds should have children before parent
      final folderIndex = result.successfulIds.indexOf('folder');
      final file1Index = result.successfulIds.indexOf('folder/file1.txt');
      final file2Index = result.successfulIds.indexOf('folder/file2.txt');
      
      expect(file1Index, lessThan(folderIndex));
      expect(file2Index, lessThan(folderIndex));

      // Verify: All items no longer exist
      expect(await repository.getEntry('folder'), isNull);
      expect(await repository.getEntry('folder/file1.txt'), isNull);
      expect(await repository.getEntry('folder/file2.txt'), isNull);

      // Verify: Delete mutations were enqueued for all items
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'delete'), isTrue);
    });
  });

  group('deleteFiles Batch Operation', () {
    late AppDatabase db;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
      service = FileOperationService(
        repository: repository,
        database: db,
      );
    });

    tearDown(() async {
      await db.close();
    });

    test('deleteFiles successfully deletes multiple files', () async {
      // Setup: Create multiple files
      await repository.upsertFile(
        const FileEntry(
          path: 'file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Delete multiple files
      final result = await service.deleteFiles(['file1.txt', 'file2.txt', 'file3.txt']);

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(3));
      expect(result.successfulIds, contains('file1.txt'));
      expect(result.successfulIds, contains('file2.txt'));
      expect(result.successfulIds, contains('file3.txt'));
      expect(result.errors, isEmpty);

      // Verify: All files no longer exist
      expect(await repository.getEntry('file1.txt'), isNull);
      expect(await repository.getEntry('file2.txt'), isNull);
      expect(await repository.getEntry('file3.txt'), isNull);

      // Verify: Delete mutations were enqueued for all files
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'delete'), isTrue);
    });

    test('deleteFiles handles partial success with non-existent files', () async {
      // Setup: Create some files (but not all)
      await repository.upsertFile(
        const FileEntry(
          path: 'file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to delete files (including non-existent one)
      final result = await service.deleteFiles(['file1.txt', 'nonexistent.txt', 'file3.txt']);

      // Verify: Partial success
      expect(result.isPartialSuccess, isTrue);
      expect(result.successfulIds, hasLength(2));
      expect(result.successfulIds, contains('file1.txt'));
      expect(result.successfulIds, contains('file3.txt'));
      expect(result.errors, hasLength(1));
      expect(result.errors.first.fileId, 'nonexistent.txt');
      expect(result.errors.first.type, FileOperationErrorType.notFound);

      // Verify: Existing files were deleted
      expect(await repository.getEntry('file1.txt'), isNull);
      expect(await repository.getEntry('file3.txt'), isNull);

      // Verify: Mutations were enqueued for successful deletes only
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'delete'), isTrue);
    });

    test('deleteFiles recursively deletes folders with children', () async {
      // Setup: Create folders with children
      await repository.upsertFile(
        const FileEntry(
          path: 'folder1',
          name: 'folder1',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder1/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder2',
          name: 'folder2',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder2/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder2/file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Delete both folders
      final result = await service.deleteFiles(['folder1', 'folder2']);

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(5)); // 2 folders + 3 files
      expect(result.successfulIds, contains('folder1'));
      expect(result.successfulIds, contains('folder1/file1.txt'));
      expect(result.successfulIds, contains('folder2'));
      expect(result.successfulIds, contains('folder2/file2.txt'));
      expect(result.successfulIds, contains('folder2/file3.txt'));
      expect(result.errors, isEmpty);

      // Verify: All folders and files no longer exist
      expect(await repository.getEntry('folder1'), isNull);
      expect(await repository.getEntry('folder1/file1.txt'), isNull);
      expect(await repository.getEntry('folder2'), isNull);
      expect(await repository.getEntry('folder2/file2.txt'), isNull);
      expect(await repository.getEntry('folder2/file3.txt'), isNull);

      // Verify: Delete mutations were enqueued for all items
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'delete'), isTrue);
    });

    test('deleteFiles continues processing after individual failures', () async {
      // Setup: Create some files
      await repository.upsertFile(
        const FileEntry(
          path: 'file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'file3.txt',
          name: 'file3.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to delete files with some non-existent
      final result = await service.deleteFiles([
        'file1.txt',
        'nonexistent1.txt',
        'file3.txt',
        'nonexistent2.txt',
      ]);

      // Verify: Partial success
      expect(result.isPartialSuccess, isTrue);
      expect(result.successfulIds, hasLength(2));
      expect(result.errors, hasLength(2));

      // Verify: Successful deletes were processed
      expect(result.successfulIds, contains('file1.txt'));
      expect(result.successfulIds, contains('file3.txt'));

      // Verify: Failures were recorded
      expect(result.errors.any((e) => e.fileId == 'nonexistent1.txt'), isTrue);
      expect(result.errors.any((e) => e.fileId == 'nonexistent2.txt'), isTrue);

      // Verify: Mutations were enqueued for successful deletes only
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
    });

    test('deleteFiles returns all failures when all operations fail', () async {
      // Execute: Try to delete non-existent files
      final result = await service.deleteFiles([
        'nonexistent1.txt',
        'nonexistent2.txt',
        'nonexistent3.txt',
      ]);

      // Verify: All operations failed
      expect(result.isFailure, isTrue);
      expect(result.successfulIds, isEmpty);
      expect(result.errors, hasLength(3));
      expect(result.errors.every((e) => e.type == FileOperationErrorType.notFound), isTrue);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('deleteFiles handles empty file list', () async {
      // Execute: Delete empty list
      final result = await service.deleteFiles([]);

      // Verify: Operation completed with no items (not a success, not a failure)
      expect(result.isSuccess, isFalse);
      expect(result.isFailure, isFalse);
      expect(result.isPartialSuccess, isFalse);
      expect(result.successfulIds, isEmpty);
      expect(result.errors, isEmpty);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });
  });

  group('copyFiles Batch Operation', () {
    late AppDatabase db;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
      service = FileOperationService(
        repository: repository,
        database: db,
      );
    });

    tearDown(() async {
      await db.close();
    });

    test('copyFiles successfully copies multiple files to target folder', () async {
      // Setup: Create source files and target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Copy files to target folder
      final result = await service.copyFiles(
        ['source/file1.txt', 'source/file2.txt'],
        'target',
      );

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(2));
      expect(result.successfulIds, contains('target/file1.txt'));
      expect(result.successfulIds, contains('target/file2.txt'));
      expect(result.errors, isEmpty);

      // Verify: Original files still exist
      expect(await repository.getEntry('source/file1.txt'), isNotNull);
      expect(await repository.getEntry('source/file2.txt'), isNotNull);

      // Verify: Copied files exist in target
      expect(await repository.getEntry('target/file1.txt'), isNotNull);
      expect(await repository.getEntry('target/file2.txt'), isNotNull);

      // Verify: Create mutations were enqueued for both files
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'create'), isTrue);
    });

    test('copyFiles handles name conflicts with unique suffixes', () async {
      // Setup: Create source files, target folder, and conflicting files
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Copy files to target folder (one has conflict)
      final result = await service.copyFiles(
        ['source/file.txt', 'source/file2.txt'],
        'target',
      );

      // Verify: Operation succeeded with unique names
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(2));
      expect(result.successfulIds, contains('target/file (1).txt')); // Conflict resolved
      expect(result.successfulIds, contains('target/file2.txt'));
      expect(result.errors, isEmpty);

      // Verify: Original files still exist
      expect(await repository.getEntry('source/file.txt'), isNotNull);
      expect(await repository.getEntry('source/file2.txt'), isNotNull);

      // Verify: Copied files exist with correct names
      expect(await repository.getEntry('target/file.txt'), isNotNull); // Original
      expect(await repository.getEntry('target/file (1).txt'), isNotNull); // Copy
      expect(await repository.getEntry('target/file2.txt'), isNotNull);

      // Verify: Create mutations were enqueued for both files
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'create'), isTrue);
    });

    test('copyFiles continues processing after individual failures', () async {
      // Setup: Create one valid source file and target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Copy files including non-existent file
      final result = await service.copyFiles(
        ['source/file1.txt', 'nonexistent.txt'],
        'target',
      );

      // Verify: Partial success
      expect(result.isPartialSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'target/file1.txt');
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.notFound);

      // Verify: Valid file was copied
      expect(await repository.getEntry('target/file1.txt'), isNotNull);

      // Verify: One create mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.first.operation, 'create');
    });

    test('copyFiles returns all failures when all operations fail', () async {
      // Setup: Create target folder only
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Try to copy non-existent files
      final result = await service.copyFiles(
        ['nonexistent1.txt', 'nonexistent2.txt'],
        'target',
      );

      // Verify: All operations failed
      expect(result.isFailure, isTrue);
      expect(result.successfulIds, isEmpty);
      expect(result.errors, hasLength(2));
      expect(result.errors.every((e) => e.type == FileOperationErrorType.notFound), isTrue);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('copyFiles handles empty file list', () async {
      // Setup: Create target folder
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );

      // Execute: Copy empty list
      final result = await service.copyFiles([], 'target');

      // Verify: Operation completed with no files (neither success nor failure)
      expect(result.successfulIds, isEmpty);
      expect(result.errors, isEmpty);
      expect(result.isSuccess, isFalse); // Empty operation is not a success
      expect(result.isFailure, isFalse); // Empty operation is not a failure

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('copyFiles resolves multiple name conflicts with incrementing suffixes', () async {
      // Setup: Create source files and target folder with existing files
      await repository.upsertFile(
        const FileEntry(
          path: 'source/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'source2/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target',
          name: 'target',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'target/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Copy two files with same name to target
      final result = await service.copyFiles(
        ['source/file.txt', 'source2/file.txt'],
        'target',
      );

      // Verify: Operation succeeded with unique names
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(2));
      expect(result.successfulIds, contains('target/file (1).txt')); // First copy
      expect(result.successfulIds, contains('target/file (2).txt')); // Second copy
      expect(result.errors, isEmpty);

      // Verify: All files exist
      expect(await repository.getEntry('target/file.txt'), isNotNull); // Original
      expect(await repository.getEntry('target/file (1).txt'), isNotNull); // First copy
      expect(await repository.getEntry('target/file (2).txt'), isNotNull); // Second copy

      // Verify: Create mutations were enqueued for both copies
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(mutations.every((m) => m.operation == 'create'), isTrue);
    });
  });

  group('renameFile Operation', () {
    late AppDatabase db;
    late ExplorerRepository repository;
    late FileOperationService service;

    setUp(() {
      db = AppDatabase.forTesting(NativeDatabase.memory());
      repository = ExplorerRepository(db: db);
      service = FileOperationService(
        repository: repository,
        database: db,
      );
    });

    tearDown(() async {
      await db.close();
    });

    test('renameFile successfully renames a file', () async {
      // Setup: Create file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/oldname.txt',
          name: 'oldname.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Rename file
      final result = await service.renameFile('folder/oldname.txt', 'newname.txt');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'folder/newname.txt');
      expect(result.errors, isEmpty);

      // Verify: Old path no longer exists
      final oldFile = await repository.getEntry('folder/oldname.txt');
      expect(oldFile, isNull);

      // Verify: New path exists with correct name
      final newFile = await repository.getEntry('folder/newname.txt');
      expect(newFile, isNotNull);
      expect(newFile!.name, 'newname.txt');
      expect(newFile.path, 'folder/newname.txt');

      // Verify: Update mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(['update', 'delete'].contains(mutations.first.operation), isTrue);
      expect(mutations.first.path, 'folder/newname.txt');
    });

    test('renameFile successfully renames a file in root', () async {
      // Setup: Create file in root
      await repository.upsertFile(
        const FileEntry(
          path: 'oldname.txt',
          name: 'oldname.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Rename file
      final result = await service.renameFile('oldname.txt', 'newname.txt');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'newname.txt');

      // Verify: Old path no longer exists
      final oldFile = await repository.getEntry('oldname.txt');
      expect(oldFile, isNull);

      // Verify: New path exists
      final newFile = await repository.getEntry('newname.txt');
      expect(newFile, isNotNull);
      expect(newFile!.name, 'newname.txt');
    });

    test('renameFile successfully renames a folder', () async {
      // Setup: Create folder
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/oldfolder',
          name: 'oldfolder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Rename folder
      final result = await service.renameFile('parent/oldfolder', 'newfolder');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'parent/newfolder');

      // Verify: Old path no longer exists
      final oldFolder = await repository.getEntry('parent/oldfolder');
      expect(oldFolder, isNull);

      // Verify: New path exists
      final newFolder = await repository.getEntry('parent/newfolder');
      expect(newFolder, isNotNull);
      expect(newFolder!.name, 'newfolder');
    });

    test('renameFile updates descendant paths when renaming a folder', () async {
      // Setup: Create folder with children
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/oldfolder',
          name: 'oldfolder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/oldfolder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/oldfolder/subfolder',
          name: 'subfolder',
          type: 'directory',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'parent/oldfolder/subfolder/nested.txt',
          name: 'nested.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Rename folder
      final result = await service.renameFile('parent/oldfolder', 'newfolder');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);

      // Verify: Old paths no longer exist
      expect(await repository.getEntry('parent/oldfolder'), isNull);
      expect(await repository.getEntry('parent/oldfolder/file.txt'), isNull);
      expect(await repository.getEntry('parent/oldfolder/subfolder'), isNull);
      expect(await repository.getEntry('parent/oldfolder/subfolder/nested.txt'), isNull);

      // Verify: New paths exist with updated structure
      expect(await repository.getEntry('parent/newfolder'), isNotNull);
      expect(await repository.getEntry('parent/newfolder/file.txt'), isNotNull);
      expect(await repository.getEntry('parent/newfolder/subfolder'), isNotNull);
      expect(await repository.getEntry('parent/newfolder/subfolder/nested.txt'), isNotNull);

      // Verify: Update mutation was enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations.isNotEmpty, isTrue);
      expect(['update', 'delete'].contains(mutations.first.operation), isTrue);
    });

    test('renameFile returns success when new name is same as current name', () async {
      // Setup: Create file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Rename to same name
      final result = await service.renameFile('folder/file.txt', 'file.txt');

      // Verify: Operation succeeded (no-op)
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds, hasLength(1));
      expect(result.successfulIds.first, 'folder/file.txt');

      // Verify: File still exists
      final file = await repository.getEntry('folder/file.txt');
      expect(file, isNotNull);

      // Verify: No mutations were enqueued (no actual change)
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile returns error for non-existent file', () async {
      // Execute: Try to rename non-existent file
      final result = await service.renameFile('nonexistent.txt', 'newname.txt');

      // Verify: Operation failed with notFound error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.notFound);
      expect(result.errors.first.message, 'File not found');

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile returns error for name conflict', () async {
      // Setup: Create two files in same folder
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file1.txt',
          name: 'file1.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file2.txt',
          name: 'file2.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to rename file1 to file2 (conflict)
      final result = await service.renameFile('folder/file1.txt', 'file2.txt');

      // Verify: Operation failed with conflict error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.conflict);
      expect(result.errors.first.message, contains('already exists'));

      // Verify: Original file still exists with old name
      final file = await repository.getEntry('folder/file1.txt');
      expect(file, isNotNull);
      expect(file!.name, 'file1.txt');

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile returns error for invalid file name with special characters', () async {
      // Setup: Create file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to rename with invalid characters
      final result = await service.renameFile('folder/file.txt', 'invalid<name>.txt');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);
      expect(result.errors.first.message, contains('Invalid file name'));

      // Verify: File still has old name
      final file = await repository.getEntry('folder/file.txt');
      expect(file, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile returns error for reserved file name', () async {
      // Setup: Create file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to rename to reserved name
      final result = await service.renameFile('folder/file.txt', 'CON');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);
      expect(result.errors.first.message, contains('Invalid file name'));

      // Verify: File still has old name
      final file = await repository.getEntry('folder/file.txt');
      expect(file, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile returns error for empty file name', () async {
      // Setup: Create file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to rename to empty name
      final result = await service.renameFile('folder/file.txt', '');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);

      // Verify: File still has old name
      final file = await repository.getEntry('folder/file.txt');
      expect(file, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile returns error for name ending with period', () async {
      // Setup: Create file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to rename to name ending with period
      final result = await service.renameFile('folder/file.txt', 'newname.');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);

      // Verify: File still has old name
      final file = await repository.getEntry('folder/file.txt');
      expect(file, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile returns error for name ending with space', () async {
      // Setup: Create file
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/file.txt',
          name: 'file.txt',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Try to rename to name ending with space
      final result = await service.renameFile('folder/file.txt', 'newname ');

      // Verify: Operation failed with validation error
      expect(result.isFailure, isTrue);
      expect(result.errors, hasLength(1));
      expect(result.errors.first.type, FileOperationErrorType.validation);

      // Verify: File still has old name
      final file = await repository.getEntry('folder/file.txt');
      expect(file, isNotNull);

      // Verify: No mutations were enqueued
      final mutations = await db.getPendingMutations();
      expect(mutations, isEmpty);
    });

    test('renameFile handles files with multiple dots correctly', () async {
      // Setup: Create file with multiple dots
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/archive.tar.gz',
          name: 'archive.tar.gz',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Rename file
      final result = await service.renameFile('folder/archive.tar.gz', 'backup.tar.gz');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds.first, 'folder/backup.tar.gz');

      // Verify: New file exists
      final newFile = await repository.getEntry('folder/backup.tar.gz');
      expect(newFile, isNotNull);
      expect(newFile!.name, 'backup.tar.gz');
    });

    test('renameFile handles files without extensions correctly', () async {
      // Setup: Create file without extension
      await repository.upsertFile(
        const FileEntry(
          path: 'folder/README',
          name: 'README',
          type: 'file',
          lastModified: '2024-01-01T00:00:00Z',
          serverVersion: 1,
        ),
      );

      // Execute: Rename file
      final result = await service.renameFile('folder/README', 'LICENSE');

      // Verify: Operation succeeded
      expect(result.isSuccess, isTrue);
      expect(result.successfulIds.first, 'folder/LICENSE');

      // Verify: New file exists
      final newFile = await repository.getEntry('folder/LICENSE');
      expect(newFile, isNotNull);
      expect(newFile!.name, 'LICENSE');
    });
  });
}

// Helper functions that replicate the validation logic for testing
// These match the private methods in FileOperationService
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

bool _isValidFileName(String name) {
  if (name.trim().isEmpty) return false;
  final invalidChars = RegExp(r'[<>:"/\\|?*\x00-\x1F]');
  if (invalidChars.hasMatch(name)) return false;
  final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
  if (reservedNames.contains(name.toUpperCase())) return false;
  if (name.endsWith('.') || name.endsWith(' ')) return false;
  return true;
}
