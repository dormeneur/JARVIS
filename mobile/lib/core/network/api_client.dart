import 'package:dio/dio.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';

/// Dio-based HTTP client with JWT Authorization header injection
/// and 401 detection.
class ApiClient {
  final Dio dio;
  final SecureStorage _secureStorage;

  /// Called when a 401 is received — app should navigate to login.
  void Function()? onUnauthorized;

  ApiClient({
    required SecureStorage secureStorage,
    Dio? dioOverride,
    this.onUnauthorized,
  }) : _secureStorage = secureStorage,
       dio = dioOverride ?? Dio() {
    dio.options.connectTimeout = const Duration(seconds: 10);
    dio.options.receiveTimeout = const Duration(seconds: 30);
    dio.options.sendTimeout = const Duration(seconds: 30);

    dio.interceptors.add(
      InterceptorsWrapper(
        onRequest: (options, handler) async {
          final jwt = await _secureStorage.getJwt();
          if (jwt != null && jwt.isNotEmpty) {
            options.headers['Authorization'] = 'Bearer $jwt';
          }
          handler.next(options);
        },
        onError: (error, handler) {
          if (error.response?.statusCode == 401) {
            onUnauthorized?.call();
          }
          handler.next(error);
        },
      ),
    );
  }

  /// Set the base URL (called after user enters server URL).
  void setBaseUrl(String url) {
    dio.options.baseUrl = url.endsWith('/')
        ? url.substring(0, url.length - 1)
        : url;
  }

  /// Initialize base URL from secure storage.
  Future<void> init() async {
    final url = await _secureStorage.getServerUrl();
    if (url != null && url.isNotEmpty) {
      setBaseUrl(url);
    }
  }
}
