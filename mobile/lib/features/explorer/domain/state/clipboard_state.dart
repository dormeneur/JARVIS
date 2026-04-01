import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/explorer/domain/models/file_operation_result.dart';
import 'package:jarvis_mobile/features/explorer/domain/services/file_operation_service.dart';

/// Represents the type of clipboard operation.
enum ClipboardOperation {
  /// Cut operation - files will be moved on paste
  cut,
  
  /// Copy operation - files will be copied on paste
  copy,
}

/// Represents the clipboard state for cut/copy/paste operations.
///
/// Tracks which files are in the clipboard and what operation (cut or copy)
/// should be performed when pasting.
class ClipboardState {
  /// List of file IDs in the clipboard
  final List<String> fileIds;

  /// The operation type (cut or copy), null if clipboard is empty
  final ClipboardOperation? operation;

  const ClipboardState({
    this.fileIds = const [],
    this.operation,
  });

  /// Creates a copy of this state with the given fields replaced
  ClipboardState copyWith({
    List<String>? fileIds,
    ClipboardOperation? operation,
  }) {
    return ClipboardState(
      fileIds: fileIds ?? this.fileIds,
      operation: operation ?? this.operation,
    );
  }

  /// Check if the clipboard is empty
  bool get isEmpty => fileIds.isEmpty;

  /// Check if the clipboard is not empty
  bool get isNotEmpty => fileIds.isNotEmpty;

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) return true;

    return other is ClipboardState &&
        other.fileIds.length == fileIds.length &&
        _listEquals(other.fileIds, fileIds) &&
        other.operation == operation;
  }

  @override
  int get hashCode => Object.hash(
        fileIds.length,
        operation,
      );

  /// Helper method to compare two lists for equality
  bool _listEquals(List<String> a, List<String> b) {
    if (a.length != b.length) return false;
    for (int i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }
}

/// State notifier for managing clipboard state.
///
/// Provides methods for cut, copy, paste, and clear operations.
/// The clipboard persists across folder navigation until explicitly cleared
/// or a paste-after-cut operation completes.
class ClipboardStateNotifier extends StateNotifier<ClipboardState> {
  ClipboardStateNotifier() : super(const ClipboardState());

  /// Store files for cut operation.
  ///
  /// Replaces any previous clipboard contents with the provided file IDs
  /// and sets the operation type to cut (move).
  void cut(List<String> fileIds) {
    state = ClipboardState(
      fileIds: List<String>.from(fileIds),
      operation: ClipboardOperation.cut,
    );
  }

  /// Store files for copy operation.
  ///
  /// Replaces any previous clipboard contents with the provided file IDs
  /// and sets the operation type to copy.
  void copy(List<String> fileIds) {
    state = ClipboardState(
      fileIds: List<String>.from(fileIds),
      operation: ClipboardOperation.copy,
    );
  }

  /// Clear the clipboard.
  ///
  /// Removes all files from the clipboard and resets the operation type.
  void clear() {
    state = const ClipboardState(
      fileIds: [],
      operation: null,
    );
  }

  /// Execute paste operation.
  ///
  /// Performs either a move or copy operation based on the clipboard's
  /// operation type. For cut operations, clears the clipboard after a
  /// successful paste. For copy operations, retains the clipboard contents.
  ///
  /// Returns [FileOperationResult] with success or error details.
  Future<FileOperationResult> paste(
    String targetFolderId,
    FileOperationService fileService,
  ) async {
    // If clipboard is empty, return an error
    if (state.isEmpty) {
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: '',
            fileName: '',
            message: 'Clipboard is empty',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    // Perform the appropriate operation based on clipboard type
    final FileOperationResult result;
    
    if (state.operation == ClipboardOperation.cut) {
      // Move files for cut operation
      result = await fileService.moveFiles(state.fileIds, targetFolderId);
      
      // Clear clipboard after successful cut-paste (move)
      if (result.isSuccess || result.isPartialSuccess) {
        clear();
      }
    } else if (state.operation == ClipboardOperation.copy) {
      // Copy files for copy operation
      result = await fileService.copyFiles(state.fileIds, targetFolderId);
      
      // Keep clipboard contents after copy-paste
      // (user can paste multiple times)
    } else {
      // Should never happen, but handle gracefully
      return FileOperationResult(
        errors: [
          FileOperationError(
            fileId: '',
            fileName: '',
            message: 'Invalid clipboard operation',
            type: FileOperationErrorType.validation,
          ),
        ],
      );
    }

    return result;
  }
}
