/// Base error types for the JARVIS mobile app.
sealed class AppError implements Exception {
  final String message;
  final Object? cause;

  const AppError(this.message, {this.cause});

  @override
  String toString() => '$runtimeType: $message';
}

class NetworkError extends AppError {
  final int? statusCode;

  const NetworkError(super.message, {this.statusCode, super.cause});
}

class AuthError extends AppError {
  const AuthError(super.message, {super.cause});
}

class SyncError extends AppError {
  const SyncError(super.message, {super.cause});
}

class StorageError extends AppError {
  const StorageError(super.message, {super.cause});
}
