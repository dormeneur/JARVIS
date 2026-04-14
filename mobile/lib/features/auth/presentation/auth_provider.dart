import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/auth/data/auth_repository.dart';

// --- Singletons ---

final secureStorageProvider = Provider<SecureStorage>((ref) {
  return SecureStorage();
});

final apiClientProvider = Provider<ApiClient>((ref) {
  final secureStorage = ref.watch(secureStorageProvider);
  return ApiClient(secureStorage: secureStorage);
});

final appDatabaseProvider = Provider<AppDatabase>((ref) {
  return AppDatabase();
});

final authRepositoryProvider = Provider<AuthRepository>((ref) {
  return AuthRepository(
    apiClient: ref.watch(apiClientProvider),
    secureStorage: ref.watch(secureStorageProvider),
  );
});

// --- Auth State ---

enum AuthStatus { loading, authenticated, unauthenticated }

class AuthState {
  final AuthStatus status;
  final String? deviceId;
  final String? deviceName;
  final String? serverUrl;
  final bool isSecretsAuthorized;
  final String? error;

  const AuthState({
    required this.status,
    this.deviceId,
    this.deviceName,
    this.serverUrl,
    this.isSecretsAuthorized = false,
    this.error,
  });

  const AuthState.loading() : this(status: AuthStatus.loading);
  const AuthState.unauthenticated({String? error})
    : this(status: AuthStatus.unauthenticated, error: error);
  const AuthState.authenticated({
    required String deviceId,
    required String deviceName,
    required String serverUrl,
    bool isSecretsAuthorized = false,
  }) : this(
         status: AuthStatus.authenticated,
         deviceId: deviceId,
         deviceName: deviceName,
         serverUrl: serverUrl,
         isSecretsAuthorized: isSecretsAuthorized,
       );
}

class AuthNotifier extends StateNotifier<AuthState> {
  final AuthRepository _authRepo;
  final SecureStorage _secureStorage;
  final ApiClient _apiClient;

  AuthNotifier(this._authRepo, this._secureStorage, this._apiClient)
    : super(const AuthState.loading());

  /// Called on app startup: load credentials and validate.
  Future<void> initialize() async {
    state = const AuthState.loading();

    final hasCredentials = await _authRepo.hasStoredCredentials();
    if (!hasCredentials) {
      state = const AuthState.unauthenticated();
      return;
    }

    await _apiClient.init();
    final validationResult = await _authRepo.validateToken();
    if (validationResult == TokenValidationResult.invalid) {
      state = const AuthState.unauthenticated(
        error: 'Session expired. Please log in again.',
      );
      return;
    }

    final deviceId = await _secureStorage.getDeviceId() ?? '';
    final deviceName = await _secureStorage.getDeviceName() ?? '';
    final serverUrl = await _secureStorage.getServerUrl() ?? '';

    final deviceInfo = await _authRepo.listDevices().then((list) => list.firstWhere((d) => d['device_id'] == deviceId));
    final isAuth = deviceInfo['is_secrets_authorized'] as bool? ?? false;

    state = AuthState.authenticated(
      deviceId: deviceId,
      deviceName: deviceName,
      serverUrl: serverUrl,
      isSecretsAuthorized: isAuth,
    );
  }

  /// Register the first device.
  Future<void> registerFirst({
    required String serverUrl,
    required String deviceName,
    required String setupSecret,
  }) async {
    state = const AuthState.loading();
    try {
      await _authRepo.registerFirstDevice(
        serverUrl: serverUrl,
        deviceName: deviceName,
        setupSecret: setupSecret,
      );
      state = AuthState.authenticated(
        deviceId: await _secureStorage.getDeviceId() ?? '',
        deviceName: deviceName,
        serverUrl: serverUrl,
        isSecretsAuthorized: true, // First device is always authorized
      );
    } catch (e) {
      state = AuthState.unauthenticated(error: e.toString());
    }
  }

  /// Register additional device.
  Future<void> registerAdditional({
    required String serverUrl,
    required String existingToken,
    required String deviceName,
  }) async {
    state = const AuthState.loading();
    try {
      await _authRepo.registerAdditionalDevice(
        serverUrl: serverUrl,
        existingToken: existingToken,
        deviceName: deviceName,
      );
      state = AuthState.authenticated(
        deviceId: await _secureStorage.getDeviceId() ?? '',
        deviceName: deviceName,
        serverUrl: serverUrl,
        isSecretsAuthorized: false, // Additional devices are not authorized by default
      );
    } catch (e) {
      state = AuthState.unauthenticated(error: e.toString());
    }
  }

  /// Reconnect to an existing device when app data is cleared.
  /// This allows re-logging in to a device already registered on the server.
  Future<void> reconnect({
    required String serverUrl,
    required String deviceName,
    required String deviceSecret,
  }) async {
    state = const AuthState.loading();
    try {
      await _authRepo.reconnectDevice(
        serverUrl: serverUrl,
        deviceName: deviceName,
        deviceSecret: deviceSecret,
      );
      final deviceId = await _secureStorage.getDeviceId();
      final devices = await _authRepo.listDevices();
      final device = devices.firstWhere((d) => d['device_id'] == deviceId);
      
      state = AuthState.authenticated(
        deviceId: deviceId ?? '',
        deviceName: deviceName,
        serverUrl: serverUrl,
        isSecretsAuthorized: device['is_secrets_authorized'] as bool? ?? false,
      );
    } catch (e) {
      state = AuthState.unauthenticated(error: e.toString());
    }
  }

  /// Logout.
  Future<void> logout() async {
    await _authRepo.logout();
    state = const AuthState.unauthenticated();
  }
}

final authProvider = StateNotifierProvider<AuthNotifier, AuthState>((ref) {
  return AuthNotifier(
    ref.watch(authRepositoryProvider),
    ref.watch(secureStorageProvider),
    ref.watch(apiClientProvider),
  );
});
