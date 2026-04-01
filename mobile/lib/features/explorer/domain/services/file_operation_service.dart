import 'package:drift/drift.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/features/explorer/domain/models/file_operation_result.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

/// Primary service for file operations with validation and error handling.
///
/// This service orchestrates complex file operations (move, copy, delete, rename)
/// while maintaining data integrity and ensuring proper synchronization with the server.
///
/// All operations:
/// - Validate inputs before making changes
/// - Update the local database
/// - Enqueue mutations for server sync
/// - Return detailed results with success/failure information
class FileOperationService {
  final ExplorerRepository _repository;
  final AppDatabase _database;

  FileOperationService({
    required ExplorerRepository repository,
    required AppDatabase database,
  })  : _repository = repository,
        _database = database;

  /// Move a single file to target folder.
  ///
  /// Validates the move operation (prevents circular moves, checks for conflicts),
  /// updates the file path in the database, and enqueues a move mutation.
  ///
  /// Returns [FileOperationResult] with success or error details.
  Future<FileOperationResult> moveFile(
    String filePath,
    String targetFolderPath,
  ) async {
    // Validate paths
    if (!_isValidPath(filePath)) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'Invalid source file path',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    // Empty target path is valid — represents root
    if (targetFolderPath.isNotEmpty && !_isValidPath(targetFolderPath)) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'Invalid target folder path',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    // Get the source file
    final file = await _repository.getEntry(filePath);
    if (file == null) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'File not found',
            type: FileOperationErrorType.notFound,
          ),
        ],
      );
    }

    // Check if target folder exists (empty path represents root, which always exists)
    if (targetFolderPath.isNotEmpty) {
      final targetFolder = await _repository.getEntry(targetFolderPath);
      if (targetFolder == null) {
        return FileOperationResult(
          errors: [
            FileOperationError(
              fileId: filePath,
              fileName: file.name,
              message: 'Target folder not found',
              type: FileOperationErrorType.notFound,
            ),
          ],
        );
      }

      if (!targetFolder.isDirectory) {
        return FileOperationResult(
          errors: [
            FileOperationError(
              fileId: filePath,
              fileName: file.name,
              message: 'Target must be a folder',
              type: FileOperationErrorType.validation,
            ),
          ],
        );
      }
    }

    // Check for circular move
    if (await _isCircularMove(filePath, targetFolderPath)) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: file.name,
            message: 'Cannot move a folder into itself or its descendants',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    // Check for name conflict in target folder
    final newPath = targetFolderPath.isEmpty 
        ? file.name 
        : '$targetFolderPath/${file.name}';
    
    final existingFile = await _repository.getEntry(newPath);
    if (existingFile != null) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: file.name,
            message: 'A file with the same name already exists in the target folder',
            type: FileOperationErrorType.conflict,
          ),
        ],
      );
    }

    // Perform the move operation in a transaction
    try {
      await _database.transaction(() async {
        // Get the current server version for the file
        final entry = await _database.getEntry(filePath);
        final baseVersion = entry?.serverVersion ?? 1;

        // If moving a folder, we need to update all descendant paths
        if (file.isDirectory) {
          // Get all descendants before moving
          final descendants = await _getAllDescendantEntries(filePath);
          
          // Delete the old folder entry
          await _database.deleteEntry(filePath);

          // Insert the new folder entry with updated path
          await _database.upsertEntry(
            FileCacheEntriesCompanion(
              path: Value(newPath),
              name: Value(file.name),
              type: Value(file.type),
              sizeBytes: Value(file.sizeBytes),
              lastModified: Value(file.lastModified),
              contentHash: Value(file.contentHash),
              localPath: Value(file.localPath),
              lastSynced: Value(file.lastSynced),
              serverVersion: Value(file.serverVersion),
            ),
          );

          // Enqueue delete for old folder and update for new folder
          await _database.removeMutationsForPath(filePath);
          await _database.enqueueMutation(
            id: 'del-${DateTime.now().millisecondsSinceEpoch}-${filePath.hashCode}',
            path: filePath,
            operation: 'delete',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: baseVersion,
          );
          await _database.enqueueMutation(
            id: 'new-${DateTime.now().millisecondsSinceEpoch}-${newPath.hashCode}',
            path: newPath,
            operation: 'update',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: 1,
          );

          // Update all descendant paths
          for (final descendant in descendants) {
            // Calculate the new path by replacing the old folder path with the new one
            final relativePath = descendant.path.substring(filePath.length);
            final newDescendantPath = '$newPath$relativePath';
            
            // Delete old entry
            await _database.deleteEntry(descendant.path);
            
            // Insert with new path
            await _database.upsertEntry(
              FileCacheEntriesCompanion(
                path: Value(newDescendantPath),
                name: Value(descendant.name),
                type: Value(descendant.type),
                sizeBytes: Value(descendant.sizeBytes),
                lastModified: Value(descendant.lastModified),
                contentHash: Value(descendant.contentHash),
                localPath: Value(descendant.localPath),
                lastSynced: Value(descendant.lastSynced),
                serverVersion: Value(descendant.serverVersion),
              ),
            );

            // Enqueue delete for old descendant and update for new descendant
            await _database.removeMutationsForPath(descendant.path);
            await _database.enqueueMutation(
              id: 'del-${DateTime.now().millisecondsSinceEpoch}-${descendant.path.hashCode}',
              path: descendant.path,
              operation: 'delete',
              timestamp: DateTime.now().toUtc().toIso8601String(),
              baseVersion: descendant.serverVersion,
            );
            await _database.enqueueMutation(
              id: 'new-${DateTime.now().millisecondsSinceEpoch}-${newDescendantPath.hashCode}',
              path: newDescendantPath,
              operation: 'update',
              timestamp: DateTime.now().toUtc().toIso8601String(),
              baseVersion: 1,
            );
          }
        } else {
          // For files, just move the single entry
          // Delete the old entry
          await _database.deleteEntry(filePath);

          // Insert the new entry with updated path
          await _database.upsertEntry(
            FileCacheEntriesCompanion(
              path: Value(newPath),
              name: Value(file.name),
              type: Value(file.type),
              sizeBytes: Value(file.sizeBytes),
              lastModified: Value(file.lastModified),
              contentHash: Value(file.contentHash),
              localPath: Value(file.localPath),
              lastSynced: Value(file.lastSynced),
              serverVersion: Value(file.serverVersion),
            ),
          );

          // Enqueue delete for old path and update for new path
          await _database.removeMutationsForPath(filePath);
          await _database.enqueueMutation(
            id: 'del-${DateTime.now().millisecondsSinceEpoch}-${filePath.hashCode}',
            path: filePath,
            operation: 'delete',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: baseVersion,
          );
          await _database.enqueueMutation(
            id: 'new-${DateTime.now().millisecondsSinceEpoch}-${newPath.hashCode}',
            path: newPath,
            operation: 'update',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: 1,
          );
        }
      });

      return FileOperationResult(
        successfulIds: [newPath],
      );
    } catch (e) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: file.name,
            message: 'Failed to move file: $e',
            type: FileOperationErrorType.system,
          ),
        ],
      );
    }
  }

  /// Move multiple files to target folder.
  ///
  /// Processes each file individually, continuing on failures to maximize
  /// success rate. Returns a result indicating which files succeeded and
  /// which failed with individual error messages.
  ///
  /// Returns [FileOperationResult] with lists of successful IDs and errors.
  Future<FileOperationResult> moveFiles(
    List<String> filePaths,
    String targetFolderPath,
  ) async {
    final successfulIds = <String>[];
    final errors = <FileOperationError>[];

    // Process each file sequentially
    for (final filePath in filePaths) {
      final result = await moveFile(filePath, targetFolderPath);
      
      if (result.isSuccess) {
        // Add the new path (after move) to successful IDs
        successfulIds.addAll(result.successfulIds);
      } else {
        // Collect errors but continue processing remaining files
        errors.addAll(result.errors);
      }
    }

    return FileOperationResult(
      successfulIds: successfulIds,
      errors: errors,
    );
  }

  /// Copy a single file to target folder.
  ///
  /// Creates a new file entry with a unique ID and the same content.
  /// If a name conflict exists, appends a numeric suffix to create a unique name.
  ///
  /// Returns [FileOperationResult] with success or error details.
  Future<FileOperationResult> copyFile(
    String filePath,
    String targetFolderPath,
  ) async {
    // Validate paths (empty target path is valid - represents root)
    if (!_isValidPath(filePath)) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'Invalid source file path',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    if (targetFolderPath.isNotEmpty && !_isValidPath(targetFolderPath)) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'Invalid target folder path',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    // Get the source file
    final file = await _repository.getEntry(filePath);
    if (file == null) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'File not found',
            type: FileOperationErrorType.notFound,
          ),
        ],
      );
    }

    // Check if target folder exists (empty path represents root, which always exists)
    if (targetFolderPath.isNotEmpty) {
      final targetFolder = await _repository.getEntry(targetFolderPath);
      if (targetFolder == null) {
        return FileOperationResult(
          errors: [
            FileOperationError(
              fileId: filePath,
              fileName: file.name,
              message: 'Target folder not found',
              type: FileOperationErrorType.notFound,
            ),
          ],
        );
      }

      if (!targetFolder.isDirectory) {
        return FileOperationResult(
          errors: [
            FileOperationError(
              fileId: filePath,
              fileName: file.name,
              message: 'Target must be a folder',
              type: FileOperationErrorType.validation,
            ),
          ],
        );
      }
    }

    // Generate unique name if there's a conflict
    final uniqueName = await generateUniqueName(file.name, targetFolderPath);
    
    // Calculate the new path
    final newPath = targetFolderPath.isEmpty 
        ? uniqueName 
        : '$targetFolderPath/$uniqueName';

    // Perform the copy operation in a transaction
    try {
      await _database.transaction(() async {
        // If copying a folder, we need to copy all descendants recursively
        if (file.isDirectory) {
          // Insert the new folder entry
          await _database.upsertEntry(
            FileCacheEntriesCompanion(
              path: Value(newPath),
              name: Value(uniqueName),
              type: Value(file.type),
              sizeBytes: Value(file.sizeBytes),
              lastModified: Value(file.lastModified),
              contentHash: Value(file.contentHash),
              localPath: Value(file.localPath),
              lastSynced: Value(file.lastSynced),
              serverVersion: const Value(1), // New copy starts at version 1
            ),
          );

          // Enqueue create mutation for the folder itself
          await _database.enqueueMutation(
            id: 'create-${DateTime.now().millisecondsSinceEpoch}-${newPath.hashCode}',
            path: newPath,
            operation: 'create',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: 1,
          );

          // Get all descendants and copy them
          final descendants = await _getAllDescendantEntries(filePath);
          
          for (final descendant in descendants) {
            // Calculate the new path by replacing the old folder path with the new one
            final relativePath = descendant.path.substring(filePath.length);
            final newDescendantPath = '$newPath$relativePath';
            
            // Insert the copied descendant
            await _database.upsertEntry(
              FileCacheEntriesCompanion(
                path: Value(newDescendantPath),
                name: Value(descendant.name),
                type: Value(descendant.type),
                sizeBytes: Value(descendant.sizeBytes),
                lastModified: Value(descendant.lastModified),
                contentHash: Value(descendant.contentHash),
                localPath: Value(descendant.localPath),
                lastSynced: Value(descendant.lastSynced),
                serverVersion: const Value(1), // New copy starts at version 1
              ),
            );

            // Enqueue create mutation for descendant
            await _database.enqueueMutation(
              id: 'create-${DateTime.now().millisecondsSinceEpoch}-${newDescendantPath.hashCode}',
              path: newDescendantPath,
              operation: 'create',
              timestamp: DateTime.now().toUtc().toIso8601String(),
              baseVersion: 1,
            );
          }
        } else {
          // For files, just copy the single entry
          await _database.upsertEntry(
            FileCacheEntriesCompanion(
              path: Value(newPath),
              name: Value(uniqueName),
              type: Value(file.type),
              sizeBytes: Value(file.sizeBytes),
              lastModified: Value(file.lastModified),
              contentHash: Value(file.contentHash),
              localPath: Value(file.localPath),
              lastSynced: Value(file.lastSynced),
              serverVersion: const Value(1), // New copy starts at version 1
            ),
          );

          // Enqueue create mutation for file
          await _database.enqueueMutation(
            id: 'create-${DateTime.now().millisecondsSinceEpoch}-${newPath.hashCode}',
            path: newPath,
            operation: 'create',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: 1,
          );
        }
      });

      return FileOperationResult(
        successfulIds: [newPath],
      );
    } catch (e) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: file.name,
            message: 'Failed to copy file: $e',
            type: FileOperationErrorType.system,
          ),
        ],
      );
    }
  }

  /// Copy multiple files to target folder.
  ///
  /// Processes each file individually, automatically resolving name conflicts
  /// with numeric suffixes. Continues on failures to maximize success rate.
  ///
  /// Returns [FileOperationResult] with lists of successful IDs and errors.
  Future<FileOperationResult> copyFiles(
    List<String> filePaths,
    String targetFolderPath,
  ) async {
    final successfulIds = <String>[];
    final errors = <FileOperationError>[];

    // Process each file sequentially
    for (final filePath in filePaths) {
      final result = await copyFile(filePath, targetFolderPath);
      
      if (result.isSuccess) {
        // Add the new path (after copy) to successful IDs
        successfulIds.addAll(result.successfulIds);
      } else {
        // Collect errors but continue processing remaining files
        errors.addAll(result.errors);
      }
    }

    return FileOperationResult(
      successfulIds: successfulIds,
      errors: errors,
    );
  }

  /// Delete a single file (recursive for folders).
  ///
  /// If the file is a folder, recursively deletes all contained files and
  /// subfolders before deleting the folder itself. Updates the database
  /// and enqueues delete mutations for each deleted item.
  ///
  /// Returns [FileOperationResult] with success or error details.
  Future<FileOperationResult> deleteFile(String filePath) async {
    // Wrap the recursive deletion in a transaction for atomicity
    try {
      final result = await _database.transaction(() async {
        return await _deleteFileRecursive(filePath);
      });
      return result;
    } catch (e) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'Failed to delete file: $e',
            type: FileOperationErrorType.system,
          ),
        ],
      );
    }
  }

  /// Recursively delete a file or folder.
  ///
  /// For folders, deletes all children first before deleting the folder itself.
  /// For files, deletes directly from the database.
  /// Adds a delete mutation to the queue for each deleted item.
  ///
  /// Returns [FileOperationResult] with successful IDs and errors.
  Future<FileOperationResult> _deleteFileRecursive(String filePath) async {
    final file = await _repository.getEntry(filePath);
    if (file == null) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'File not found',
            type: FileOperationErrorType.notFound,
          ),
        ],
      );
    }

    final successfulIds = <String>[];
    final errors = <FileOperationError>[];

    if (file.isDirectory) {
      // Delete children first
      final children = await _repository.getChildren(filePath);
      for (final child in children) {
        final result = await _deleteFileRecursive(child.path);
        successfulIds.addAll(result.successfulIds);
        errors.addAll(result.errors);
      }
    }

    // Delete the file/folder itself
    try {
      // Get the current server version for the file
      final entry = await _database.getEntry(filePath);
      final baseVersion = entry?.serverVersion ?? 1;

      await _database.deleteEntry(filePath);
      
      // Clear any pending/failed mutations that might conflict with this deletion
      await _database.removeMutationsForPath(filePath);

      // Enqueue delete mutation
      await _database.enqueueMutation(
        id: 'delete-${DateTime.now().millisecondsSinceEpoch}-${filePath.hashCode}',
        path: filePath,
        operation: 'delete',
        timestamp: DateTime.now().toUtc().toIso8601String(),
        baseVersion: baseVersion,
      );
      
      successfulIds.add(filePath);
    } catch (e) {
      errors.add(
        FileOperationError(
          fileId: filePath,
          fileName: file.name,
          message: e.toString(),
          type: FileOperationErrorType.system,
        ),
      );
    }

    return FileOperationResult(
      successfulIds: successfulIds,
      errors: errors,
    );
  }

  /// Delete multiple files (recursive for folders).
  ///
  /// Processes each file individually, recursively deleting folder contents.
  /// Continues on failures to maximize success rate.
  ///
  /// Returns [FileOperationResult] with lists of successful IDs and errors.
  Future<FileOperationResult> deleteFiles(List<String> filePaths) async {
    final successfulIds = <String>[];
    final errors = <FileOperationError>[];

    // Process each file sequentially
    for (final filePath in filePaths) {
      final result = await deleteFile(filePath);
      
      if (result.isSuccess || result.isPartialSuccess) {
        // Add all successfully deleted IDs (includes descendants for folders)
        successfulIds.addAll(result.successfulIds);
      }
      
      if (result.errors.isNotEmpty) {
        // Collect errors but continue processing remaining files
        errors.addAll(result.errors);
      }
    }

    return FileOperationResult(
      successfulIds: successfulIds,
      errors: errors,
    );
  }

  /// Rename a file.
  ///
  /// Validates the new name (checks for invalid characters, reserved names,
  /// and conflicts with existing files in the same folder). Updates the
  /// file name in the database and enqueues an update mutation.
  ///
  /// Returns [FileOperationResult] with success or error details.
  Future<FileOperationResult> renameFile(
    String filePath,
    String newName,
  ) async {
    // Validate the new name
    if (!_isValidFileName(newName)) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'Invalid file name: contains invalid characters or is a reserved name',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    // Get the file to rename
    final file = await _repository.getEntry(filePath);
    if (file == null) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: filePath.split('/').last,
            message: 'File not found',
            type: FileOperationErrorType.notFound,
          ),
        ],
      );
    }

    // If the new name is the same as the current name, no operation needed
    if (file.name == newName) {
      return FileOperationResult(
        successfulIds: [filePath],
      );
    }

    // Calculate the new path
    final pathSegments = filePath.split('/');
    pathSegments.removeLast(); // Remove the old file name
    final parentPath = pathSegments.isEmpty ? '' : pathSegments.join('/');
    final newPath = parentPath.isEmpty ? newName : '$parentPath/$newName';

    // Check for name conflict in the parent folder
    final existingFile = await _repository.getEntry(newPath);
    if (existingFile != null) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: file.name,
            message: 'A file with the name "$newName" already exists in this folder',
            type: FileOperationErrorType.conflict,
          ),
        ],
      );
    }

    // Perform the rename operation in a transaction
    try {
      await _database.transaction(() async {
        // Get the current server version for the file
        final entry = await _database.getEntry(filePath);
        final baseVersion = entry?.serverVersion ?? 1;

        // If renaming a folder, we need to update all descendant paths
        if (file.isDirectory) {
          // Get all descendants before renaming
          final descendants = await _getAllDescendantEntries(filePath);
          
          // Delete the old folder entry
          await _database.deleteEntry(filePath);

          // Insert the new folder entry with updated name and path
          await _database.upsertEntry(
            FileCacheEntriesCompanion(
              path: Value(newPath),
              name: Value(newName),
              type: Value(file.type),
              sizeBytes: Value(file.sizeBytes),
              lastModified: Value(file.lastModified),
              contentHash: Value(file.contentHash),
              localPath: Value(file.localPath),
              lastSynced: Value(file.lastSynced),
              serverVersion: Value(file.serverVersion),
            ),
          );

          // Enqueue delete for old folder and update for new folder
          await _database.removeMutationsForPath(filePath);
          await _database.enqueueMutation(
            id: 'del-${DateTime.now().millisecondsSinceEpoch}-${filePath.hashCode}',
            path: filePath,
            operation: 'delete',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: baseVersion,
          );
          await _database.enqueueMutation(
            id: 'new-${DateTime.now().millisecondsSinceEpoch}-${newPath.hashCode}',
            path: newPath,
            operation: 'update',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: 1,
          );

          // Update all descendant paths
          for (final descendant in descendants) {
            // Calculate the new path by replacing the old folder path with the new one
            final relativePath = descendant.path.substring(filePath.length);
            final newDescendantPath = '$newPath$relativePath';
            
            // Delete old entry
            await _database.deleteEntry(descendant.path);
            
            // Insert with new path
            await _database.upsertEntry(
              FileCacheEntriesCompanion(
                path: Value(newDescendantPath),
                name: Value(descendant.name),
                type: Value(descendant.type),
                sizeBytes: Value(descendant.sizeBytes),
                lastModified: Value(descendant.lastModified),
                contentHash: Value(descendant.contentHash),
                localPath: Value(descendant.localPath),
                lastSynced: Value(descendant.lastSynced),
                serverVersion: Value(descendant.serverVersion),
              ),
            );

            // Enqueue mutations for descendant
            await _database.removeMutationsForPath(descendant.path);
            await _database.enqueueMutation(
              id: 'del-${DateTime.now().millisecondsSinceEpoch}-${descendant.path.hashCode}',
              path: descendant.path,
              operation: 'delete',
              timestamp: DateTime.now().toUtc().toIso8601String(),
              baseVersion: descendant.serverVersion,
            );
            await _database.enqueueMutation(
              id: 'new-${DateTime.now().millisecondsSinceEpoch}-${newDescendantPath.hashCode}',
              path: newDescendantPath,
              operation: 'update',
              timestamp: DateTime.now().toUtc().toIso8601String(),
              baseVersion: 1,
            );
          }
        } else {
          // For files, just rename the single entry
          // Delete the old entry
          await _database.deleteEntry(filePath);

          // Insert the new entry with updated name and path
          await _database.upsertEntry(
            FileCacheEntriesCompanion(
              path: Value(newPath),
              name: Value(newName),
              type: Value(file.type),
              sizeBytes: Value(file.sizeBytes),
              lastModified: Value(file.lastModified),
              contentHash: Value(file.contentHash),
              localPath: Value(file.localPath),
              lastSynced: Value(file.lastSynced),
              serverVersion: Value(file.serverVersion),
            ),
          );

          // Enqueue delete for old path and update for new path
          await _database.removeMutationsForPath(filePath);
          await _database.enqueueMutation(
            id: 'del-${DateTime.now().millisecondsSinceEpoch}-${filePath.hashCode}',
            path: filePath,
            operation: 'delete',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: baseVersion,
          );
          await _database.enqueueMutation(
            id: 'new-${DateTime.now().millisecondsSinceEpoch}-${newPath.hashCode}',
            path: newPath,
            operation: 'update',
            timestamp: DateTime.now().toUtc().toIso8601String(),
            baseVersion: 1,
          );
        }
      });

      return FileOperationResult(
        successfulIds: [newPath],
      );
    } catch (e) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: filePath,
            fileName: file.name,
            message: 'Failed to rename file: $e',
            type: FileOperationErrorType.system,
          ),
        ],
      );
    }
  }

  // Private helper methods

  /// Check if a move would be circular.
  ///
  /// A move is circular if:
  /// - Moving a folder into itself (fileId == targetFolderId)
  /// - Moving a folder into one of its descendants
  ///
  /// Returns true if the move would be circular, false otherwise.
  Future<bool> _isCircularMove(String filePath, String targetFolderPath) async {
    // Cannot move a folder into itself
    if (filePath == targetFolderPath) return true;

    // Get the file being moved
    final file = await _repository.getEntry(filePath);
    if (file == null || !file.isDirectory) return false;

    // Check if target is a descendant of the file being moved
    final descendants = await _getDescendantPaths(filePath);
    return descendants.contains(targetFolderPath);
  }

  /// Generate a unique name in the target folder.
  ///
  /// If the base name doesn't exist, returns it unchanged.
  /// Otherwise, appends numeric suffixes like " (1)", " (2)", etc.
  /// until a unique name is found.
  ///
  /// Example: "file.txt" -> "file (1).txt" -> "file (2).txt"
  ///
  /// Returns the unique name.
  /// 
  /// Note: This method is public for testing purposes but should be treated
  /// as internal to the service.
  Future<String> generateUniqueName(String baseName, String parentPath) async {
    final existingNames = await _repository.getFileNamesInPath(parentPath);

    if (!existingNames.contains(baseName)) {
      return baseName;
    }

    // Extract name and extension
    final lastDot = baseName.lastIndexOf('.');
    final name = lastDot > 0 ? baseName.substring(0, lastDot) : baseName;
    final ext = lastDot > 0 ? baseName.substring(lastDot) : '';

    // Try numeric suffixes
    int counter = 1;
    while (true) {
      final candidateName = '$name ($counter)$ext';
      if (!existingNames.contains(candidateName)) {
        return candidateName;
      }
      counter++;
    }
  }

  /// Get all descendant paths of a folder (recursive).
  ///
  /// Traverses the folder hierarchy to collect all file and folder paths
  /// that are descendants of the given folder. Used for circular move
  /// detection and recursive deletion.
  ///
  /// Returns a list of all descendant file/folder paths.
  Future<List<String>> _getDescendantPaths(String folderPath) async {
    final descendants = <String>[];
    final queue = [folderPath];

    while (queue.isNotEmpty) {
      final currentPath = queue.removeAt(0);
      final children = await _repository.getChildrenPaths(currentPath);
      descendants.addAll(children);

      // Add folders to queue for recursive traversal
      final folders = await _repository.getFolderPaths(children);
      queue.addAll(folders);
    }

    return descendants;
  }

  /// Get all descendant file entries of a folder (recursive).
  ///
  /// Similar to _getDescendantPaths but returns full FileEntry objects
  /// instead of just paths. Used when moving folders to update all
  /// descendant paths.
  ///
  /// Returns a list of all descendant FileEntry objects.
  Future<List<FileEntry>> _getAllDescendantEntries(String folderPath) async {
    final descendants = <FileEntry>[];
    final allFiles = await _database.getAllFiles();
    final prefix = '$folderPath/';

    for (final file in allFiles) {
      if (file.path.startsWith(prefix)) {
        descendants.add(FileEntry(
          path: file.path,
          name: file.name,
          type: file.type,
          sizeBytes: file.sizeBytes,
          lastModified: file.lastModified,
          contentHash: file.contentHash,
          localPath: file.localPath,
          lastSynced: file.lastSynced,
          serverVersion: file.serverVersion,
        ));
      }
    }

    return descendants;
  }

  /// Validate a file path.
  ///
  /// Checks that:
  /// - The path is not empty
  /// - The path doesn't contain invalid characters (<>:"|?*\x00-\x1F)
  /// - The path doesn't contain reserved names (CON, PRN, AUX, NUL, COM1, LPT1)
  ///
  /// Returns true if the path is valid, false otherwise.
  bool _isValidPath(String path) {
    // Check for empty path
    if (path.isEmpty) return false;

    // Check for invalid characters
    final invalidChars = RegExp(r'[<>:"|?*\x00-\x1F]');
    if (invalidChars.hasMatch(path)) return false;

    // Check for reserved names (Windows)
    final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
    final segments = path.split('/');
    for (final segment in segments) {
      if (reservedNames.contains(segment.toUpperCase())) return false;
    }

    return true;
  }

  /// Validate a file name.
  ///
  /// Checks that:
  /// - The name is not empty (after trimming)
  /// - The name doesn't contain invalid characters (<>:"/\|?*\x00-\x1F)
  /// - The name is not a reserved name (CON, PRN, AUX, NUL, COM1, LPT1)
  /// - The name doesn't end with a period or space
  ///
  /// Returns true if the name is valid, false otherwise.
  bool _isValidFileName(String name) {
    // Check for empty name
    if (name.trim().isEmpty) return false;

    // Check for invalid characters
    final invalidChars = RegExp(r'[<>:"/\\|?*\x00-\x1F]');
    if (invalidChars.hasMatch(name)) return false;

    // Check for reserved names
    final reservedNames = ['CON', 'PRN', 'AUX', 'NUL', 'COM1', 'LPT1'];
    if (reservedNames.contains(name.toUpperCase())) return false;

    // Check for names ending with period or space
    if (name.endsWith('.') || name.endsWith(' ')) return false;

    return true;
  }
}
