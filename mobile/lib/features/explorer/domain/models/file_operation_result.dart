/// Enum representing different types of file operation errors.
///
/// Used to categorize errors for appropriate handling and user feedback.
enum FileOperationErrorType {
  /// Invalid path, name, or circular move detected
  validation,

  /// Name conflict in target folder
  conflict,

  /// File or folder not found
  notFound,

  /// Permission denied
  permission,

  /// Database or file system error
  system,
}

/// Represents an error that occurred during a file operation.
///
/// Contains details about which file failed and why, enabling
/// detailed error reporting to users.
class FileOperationError {
  /// The ID of the file that encountered an error
  final String fileId;

  /// The name of the file that encountered an error
  final String fileName;

  /// A descriptive error message explaining what went wrong
  final String message;

  /// The type of error that occurred
  final FileOperationErrorType type;

  const FileOperationError({
    required this.fileId,
    required this.fileName,
    required this.message,
    required this.type,
  });

  @override
  String toString() {
    return 'FileOperationError(fileId: $fileId, fileName: $fileName, '
        'message: $message, type: $type)';
  }

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) return true;

    return other is FileOperationError &&
        other.fileId == fileId &&
        other.fileName == fileName &&
        other.message == message &&
        other.type == type;
  }

  @override
  int get hashCode {
    return fileId.hashCode ^
        fileName.hashCode ^
        message.hashCode ^
        type.hashCode;
  }
}

/// Result object for file operations with success/failure tracking.
///
/// Supports both single and batch operations, providing detailed
/// information about which files succeeded and which failed.
class FileOperationResult {
  /// List of file IDs that were successfully processed
  final List<String> successfulIds;

  /// List of errors for files that failed to process
  final List<FileOperationError> errors;

  const FileOperationResult({
    this.successfulIds = const [],
    this.errors = const [],
  });

  /// Returns true if all operations succeeded (no errors)
  bool get isSuccess => errors.isEmpty && successfulIds.isNotEmpty;

  /// Returns true if some operations succeeded and some failed
  bool get isPartialSuccess => successfulIds.isNotEmpty && errors.isNotEmpty;

  /// Returns true if all operations failed (no successes)
  bool get isFailure => successfulIds.isEmpty && errors.isNotEmpty;

  /// Returns a user-friendly message describing the operation result
  String get message {
    if (isSuccess) {
      return 'Operation completed successfully';
    } else if (isPartialSuccess) {
      return '${successfulIds.length} succeeded, ${errors.length} failed';
    } else if (isFailure) {
      return 'Operation failed: ${errors.first.message}';
    } else {
      return 'No operations performed';
    }
  }

  @override
  String toString() {
    return 'FileOperationResult(successfulIds: $successfulIds, errors: $errors)';
  }

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) return true;

    return other is FileOperationResult &&
        _listEquals(other.successfulIds, successfulIds) &&
        _listEquals(other.errors, errors);
  }

  @override
  int get hashCode => successfulIds.hashCode ^ errors.hashCode;

  bool _listEquals<T>(List<T>? a, List<T>? b) {
    if (a == null) return b == null;
    if (b == null || a.length != b.length) return false;
    for (int i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }
}
