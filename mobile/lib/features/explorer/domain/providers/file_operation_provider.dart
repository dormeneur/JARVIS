import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/services/file_operation_service.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';

/// Provider for the file operation service.
///
/// This provider creates and manages the FileOperationService instance,
/// which orchestrates complex file operations (move, copy, delete, rename)
/// with proper validation, error handling, and server synchronization.
///
/// The service depends on:
/// - ExplorerRepository: For file CRUD operations
/// - AppDatabase: For database transactions and mutation queue management
///
/// Usage:
/// ```dart
/// // Access the service to perform operations
/// final service = ref.read(fileOperationServiceProvider);
///
/// // Move a file
/// final result = await service.moveFile(fileId, targetFolderId);
///
/// // Copy multiple files
/// final result = await service.copyFiles(fileIds, targetFolderId);
///
/// // Delete files
/// final result = await service.deleteFiles(fileIds);
///
/// // Rename a file
/// final result = await service.renameFile(fileId, newName);
/// ```
final fileOperationServiceProvider = Provider<FileOperationService>((ref) {
  return FileOperationService(
    repository: ref.watch(explorerRepositoryProvider),
    database: ref.watch(appDatabaseProvider),
  );
});
