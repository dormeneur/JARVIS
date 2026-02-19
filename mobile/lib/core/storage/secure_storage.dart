import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Wrapper around flutter_secure_storage for JWT and server URL persistence.
class SecureStorage {
  static const _keyJwt = 'jarvis_jwt';
  static const _keyServerUrl = 'jarvis_server_url';
  static const _keyDeviceId = 'jarvis_device_id';
  static const _keyDeviceName = 'jarvis_device_name';

  final FlutterSecureStorage _storage;

  SecureStorage({FlutterSecureStorage? storage})
    : _storage =
          storage ??
          const FlutterSecureStorage(
            aOptions: AndroidOptions(encryptedSharedPreferences: true),
          );

  // --- JWT ---

  Future<String?> getJwt() => _storage.read(key: _keyJwt);

  Future<void> setJwt(String token) =>
      _storage.write(key: _keyJwt, value: token);

  Future<void> deleteJwt() => _storage.delete(key: _keyJwt);

  // --- Server URL ---

  Future<String?> getServerUrl() => _storage.read(key: _keyServerUrl);

  Future<void> setServerUrl(String url) =>
      _storage.write(key: _keyServerUrl, value: url);

  // --- Device Info ---

  Future<String?> getDeviceId() => _storage.read(key: _keyDeviceId);

  Future<void> setDeviceId(String id) =>
      _storage.write(key: _keyDeviceId, value: id);

  Future<String?> getDeviceName() => _storage.read(key: _keyDeviceName);

  Future<void> setDeviceName(String name) =>
      _storage.write(key: _keyDeviceName, value: name);

  // --- Clear All ---

  Future<void> clearAll() => _storage.deleteAll();
}
