import 'package:dio/dio.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/network/api_exceptions.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';

/// Handles device registration, token storage, and validation.
class AuthRepository {
  final ApiClient _apiClient;
  final SecureStorage _secureStorage;

  AuthRepository({
    required ApiClient apiClient,
    required SecureStorage secureStorage,
  }) : _apiClient = apiClient,
       _secureStorage = secureStorage;

  /// Check if server is reachable and get health status.
  Future<bool> checkServerHealth(String serverUrl) async {
    try {
      final response = await Dio().get('$serverUrl/health');
      return response.statusCode == 200;
    } catch (_) {
      return false;
    }
  }

  /// Register the first device using setup secret.
  Future<void> registerFirstDevice({
    required String serverUrl,
    required String deviceName,
    required String setupSecret,
  }) async {
    _apiClient.setBaseUrl(serverUrl);

    try {
      final response = await _apiClient.dio.post(
        '/auth/register',
        data: {'device_name': deviceName, 'setup_secret': setupSecret},
      );

      final data = response.data as Map<String, dynamic>;
      await _secureStorage.setServerUrl(serverUrl);
      await _secureStorage.setJwt(data['access_token'] as String);
      await _secureStorage.setDeviceId(data['device_id'] as String);
      await _secureStorage.setDeviceName(data['device_name'] as String);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// Register an additional device (requires existing JWT).
  Future<void> registerAdditionalDevice({
    required String serverUrl,
    required String existingToken,
    required String deviceName,
  }) async {
    _apiClient.setBaseUrl(serverUrl);

    try {
      // Temporarily set the provided token for this request
      final response = await _apiClient.dio.post(
        '/auth/register/device',
        data: {'device_name': deviceName},
        options: Options(headers: {'Authorization': 'Bearer $existingToken'}),
      );

      final data = response.data as Map<String, dynamic>;
      await _secureStorage.setServerUrl(serverUrl);
      await _secureStorage.setJwt(data['access_token'] as String);
      await _secureStorage.setDeviceId(data['device_id'] as String);
      await _secureStorage.setDeviceName(data['device_name'] as String);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// Validate the stored token by calling GET /auth/me.
  Future<bool> validateToken() async {
    try {
      await _apiClient.init();
      final jwt = await _secureStorage.getJwt();
      if (jwt == null || jwt.isEmpty) return false;

      final url = await _secureStorage.getServerUrl();
      if (url == null || url.isEmpty) return false;

      final response = await _apiClient.dio.get('/auth/me');
      return response.statusCode == 200;
    } on DioException {
      return false;
    } catch (_) {
      return false;
    }
  }

  /// Refresh the current token.
  Future<void> refreshToken() async {
    try {
      final response = await _apiClient.dio.post('/auth/refresh');
      final data = response.data as Map<String, dynamic>;
      await _secureStorage.setJwt(data['access_token'] as String);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// Logout: clear all stored credentials.
  Future<void> logout() async {
    await _secureStorage.clearAll();
  }

  /// Check if credentials exist locally.
  Future<bool> hasStoredCredentials() async {
    final jwt = await _secureStorage.getJwt();
    final url = await _secureStorage.getServerUrl();
    return jwt != null && jwt.isNotEmpty && url != null && url.isNotEmpty;
  }
}
