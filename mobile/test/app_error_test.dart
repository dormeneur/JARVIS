import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/core/errors/app_error.dart';

void main() {
  group('AppError hierarchy', () {
    test('NetworkError stores statusCode', () {
      const error = NetworkError('fail', statusCode: 401);
      expect(error.statusCode, 401);
      expect(error.message, 'fail');
      expect(error.toString(), contains('NetworkError'));
    });

    test('AuthError is an AppError', () {
      const error = AuthError('invalid token');
      expect(error, isA<AppError>());
      expect(error.message, 'invalid token');
    });

    test('SyncError is an AppError', () {
      const error = SyncError('sync failed');
      expect(error, isA<AppError>());
    });

    test('StorageError is an AppError', () {
      const error = StorageError('db error');
      expect(error, isA<AppError>());
    });
  });
}
