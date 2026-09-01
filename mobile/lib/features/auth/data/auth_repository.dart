import 'package:dio/dio.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/network/api_exceptions.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';

/// Outcome of a background token validation check.
enum TokenValidationResult { valid, invalid, unreachable }

/// Handles device registration, token storage, and validation.
class AuthRepository {
  final ApiClient _apiClient;
  final SecureStorage _secureStorage;

  AuthRepository({
    required ApiClient apiClient,
    required SecureStorage secureStorage,
  })  : _apiClient = apiClient,
        _secureStorage = secureStorage;

  // ---------------------------------------------------------------------------
  // Health
  // ---------------------------------------------------------------------------

  Future<bool> checkServerHealth(String serverUrl) async {
    try {
      final response = await Dio().get('$serverUrl/health');
      return response.statusCode == 200;
    } catch (_) {
      return false;
    }
  }

  // ---------------------------------------------------------------------------
  // Registration flows
  // ---------------------------------------------------------------------------

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
      await _storeCredentials(serverUrl, response.data as Map<String, dynamic>);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  Future<void> registerAdditionalDevice({
    required String serverUrl,
    required String existingToken,
    required String deviceName,
  }) async {
    _apiClient.setBaseUrl(serverUrl);
    try {
      final response = await _apiClient.dio.post(
        '/auth/register/device',
        data: {'device_name': deviceName},
        options: Options(headers: {'Authorization': 'Bearer $existingToken'}),
      );
      await _storeCredentials(serverUrl, response.data as Map<String, dynamic>);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  Future<void> reconnectDevice({
    required String serverUrl,
    required String deviceName,
    required String deviceSecret,
  }) async {
    _apiClient.setBaseUrl(serverUrl);
    try {
      final response = await _apiClient.dio.post(
        '/auth/reconnect',
        data: {'device_name': deviceName, 'device_secret': deviceSecret},
      );
      final data = response.data as Map<String, dynamic>;
      await _secureStorage.setServerUrl(serverUrl);
      await _secureStorage.setJwt(data['access_token'] as String);
      await _secureStorage.setDeviceId(data['device_id'] as String);
      await _secureStorage.setDeviceName(data['device_name'] as String);
      // device_secret doesn't change on reconnect
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  // ---------------------------------------------------------------------------
  // QR invite flow
  // ---------------------------------------------------------------------------

  /// Request a 10-min single-use invite token (admin devices only).
  /// Returns a record so the caller can build the QR payload.
  Future<({String inviteToken, DateTime expiresAt, int ttlSeconds, String serverUrl})>
      createInviteToken() async {
    try {
      final response = await _apiClient.dio.post('/auth/invite');
      final data = response.data as Map<String, dynamic>;
      final serverUrl = await _secureStorage.getServerUrl() ?? '';
      return (
        inviteToken: data['invite_token'] as String,
        expiresAt: DateTime.parse(data['expires_at'] as String),
        ttlSeconds: data['ttl_seconds'] as int,
        serverUrl: serverUrl,
      );
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  /// Register by redeeming a QR invite token (guest device, no JWT needed).
  Future<void> registerViaInvite({
    required String serverUrl,
    required String deviceName,
    required String inviteToken,
  }) async {
    _apiClient.setBaseUrl(serverUrl);
    try {
      final response = await _apiClient.dio.post(
        '/auth/invite/register',
        data: {
          'device_name': deviceName,
          'invite_token': inviteToken,
          'server_url': serverUrl,
        },
      );
      await _storeCredentials(serverUrl, response.data as Map<String, dynamic>);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  // ---------------------------------------------------------------------------
  // Token lifecycle
  // ---------------------------------------------------------------------------

  Future<TokenValidationResult> validateToken() async {
    try {
      await _apiClient.init();
      final jwt = await _secureStorage.getJwt();
      if (jwt == null || jwt.isEmpty) return TokenValidationResult.invalid;
      final url = await _secureStorage.getServerUrl();
      if (url == null || url.isEmpty) return TokenValidationResult.invalid;

      final response = await _apiClient.dio.get(
        '/auth/me',
        options: Options(
          sendTimeout: const Duration(seconds: 5),
          receiveTimeout: const Duration(seconds: 5),
        ),
      );
      return response.statusCode == 200
          ? TokenValidationResult.valid
          : TokenValidationResult.invalid;
    } on DioException catch (e) {
      if (e.response?.statusCode == 401 || e.response?.statusCode == 403) {
        return TokenValidationResult.invalid;
      }
      return TokenValidationResult.unreachable;
    } catch (_) {
      return TokenValidationResult.unreachable;
    }
  }

  Future<void> refreshToken() async {
    try {
      final response = await _apiClient.dio.post('/auth/refresh');
      final data = response.data as Map<String, dynamic>;
      await _secureStorage.setJwt(data['access_token'] as String);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  Future<void> logout() async {
    await _secureStorage.clearAll();
  }

  Future<bool> hasStoredCredentials() async {
    final jwt = await _secureStorage.getJwt();
    final url = await _secureStorage.getServerUrl();
    return jwt != null && jwt.isNotEmpty && url != null && url.isNotEmpty;
  }

  // ---------------------------------------------------------------------------
  // Device management
  // ---------------------------------------------------------------------------

  Future<List<Map<String, dynamic>>> listDevices() async {
    try {
      final response = await _apiClient.dio.get('/auth/devices');
      final data = response.data as Map<String, dynamic>;
      return List<Map<String, dynamic>>.from(data['devices'] as List);
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  Future<void> authorizeSecrets(String deviceId) async {
    try {
      await _apiClient.dio.post(
        '/auth/authorize_secrets',
        queryParameters: {'device_id': deviceId},
      );
    } on DioException catch (e) {
      throw mapDioError(e);
    }
  }

  // ---------------------------------------------------------------------------
  // Private helpers
  // ---------------------------------------------------------------------------

  Future<void> _storeCredentials(String serverUrl, Map<String, dynamic> data) async {
    await _secureStorage.setServerUrl(serverUrl);
    await _secureStorage.setJwt(data['access_token'] as String);
    await _secureStorage.setDeviceId(data['device_id'] as String);
    await _secureStorage.setDeviceName(data['device_name'] as String);
    if (data['device_secret'] != null) {
      await _secureStorage.setDeviceSecret(data['device_secret'] as String);
    }
  }
}
