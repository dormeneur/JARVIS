import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/network/server_routes.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';
import 'package:jarvis_mobile/features/auth/data/auth_repository.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

final vaultApiProvider = Provider<VaultApi>((ref) => VaultApi(
      ref.watch(apiClientProvider),
      ref.watch(authRepositoryProvider),
      ref.watch(secureStorageProvider),
    ));

/// An entry in a vault folder, as jv-api lists it.
class VaultItem {
  const VaultItem(this.path, this.name, this.isFolder);

  final String path;
  final String name;
  final bool isFolder;
}

/// The vault through jv-api for features that read and write single files (About me, hunches):
/// versioned writes, so every device syncs them; an expired session is renewed once.
class VaultApi {
  /// The address that answers now, saved as the one to use; null when none does.
  Future<String?> reroute() async {
    final candidates = ServerRoutes.candidates(await _storage.getServerUrl(), await _storage.getRoutes());
    final working = await ServerRoutes.firstAnswering(candidates, _auth.checkServerHealth);
    if (working != null) {
      await _storage.setServerUrl(working);
      _api.setBaseUrl(working);
    }
    return working;
  }

  static bool _unreachable(DioException e) =>
      e.response == null && (e.type == DioExceptionType.connectionError || e.type == DioExceptionType.connectionTimeout || e.type == DioExceptionType.unknown);

  VaultApi(this._api, this._auth, this._storage);

  final ApiClient _api;
  final AuthRepository _auth;
  final SecureStorage _storage;

  Dio get dio => _api.dio;

  Future<List<VaultItem>> list(String folder) async {
    try {
      final response = await call(() => _api.dio.get('/files/${Uri.encodeFull(folder)}'));
      final entries = (response.data as Map)['entries'] as List? ?? const [];
      return [
        for (final entry in entries.whereType<Map>())
          VaultItem(entry['path'] as String, entry['name'] as String, entry['type'] == 'directory'),
      ];
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return const [];
      rethrow;
    }
  }

  Future<String?> read(String path) async {
    try {
      final response = await call(() => _api.dio.get('/files/${Uri.encodeFull(path)}'));
      return (response.data as Map)['content'] as String?;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  /// Creates or replaces a file (the server makes missing folders).
  Future<void> write(String path, String content) async {
    final encoded = Uri.encodeFull(path);
    try {
      await call(() => _api.dio.put('/files/$encoded', data: {'content': content}));
    } on DioException catch (e) {
      if (e.response?.statusCode != 404) rethrow;
      await call(() => _api.dio.post('/files/$encoded', data: {'content': content, 'type': 'file'}));
    }
  }

  /// Deletes a file, or a folder with everything in it (file by file, so version records go too).
  Future<void> delete(String path, {bool folder = false}) async {
    if (folder) {
      for (final item in await list(path)) {
        await delete(item.path, folder: item.isFolder);
      }
    }
    try {
      await call(() => _api.dio.delete('/files/${Uri.encodeFull(path)}'));
    } on DioException catch (e) {
      if (e.response?.statusCode != 404) rethrow;
    }
  }

  /// One request with the stored session. An expired session is renewed once with the device
  /// secret; an unreachable address is swapped once for whichever known route answers (Tailscale,
  /// Wi-Fi, USB), so the sheets over other apps find the PC like the app does.
  Future<Response<dynamic>> call(Future<Response<dynamic>> Function() request) async {
    await _api.init();
    try {
      return await request();
    } on DioException catch (e) {
      if (_unreachable(e) && await reroute() != null) return await request();
      if (e.response?.statusCode != 401) rethrow;
      final name = await _storage.getDeviceName();
      final secret = await _storage.getDeviceSecret();
      final url = await _storage.getServerUrl();
      if (name == null || secret == null || url == null) rethrow;
      await _auth.reconnectDevice(serverUrl: url, deviceName: name, deviceSecret: secret);
      return await request();
    }
  }
}
