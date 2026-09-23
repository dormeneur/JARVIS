import 'dart:convert';

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

  /// Called on app startup: load credentials and proceed immediately.
  /// Token validation happens in the background — never blocks the UI.
  Future<void> initialize() async {
    state = const AuthState.loading();

    // Step 1: Check for stored credentials locally (no network needed)
    final hasCredentials = await _authRepo.hasStoredCredentials();
    if (!hasCredentials) {
      state = const AuthState.unauthenticated();
      return;
    }

    // Step 2: Load local device info and proceed IMMEDIATELY
    await _apiClient.init();
    final deviceId = await _secureStorage.getDeviceId() ?? '';
    final deviceName = await _secureStorage.getDeviceName() ?? '';
    final serverUrl = await _secureStorage.getServerUrl() ?? '';

    // Proceed to authenticated state using locally stored data.
    // We trust local credentials and don't block on network.
    state = AuthState.authenticated(
      deviceId: deviceId,
      deviceName: deviceName,
      serverUrl: serverUrl,
      isSecretsAuthorized: false, // Conservative default; updated when online
    );

    // Step 3: Validate token in background (non-blocking, fire-and-forget)
    _validateInBackground();
  }

  /// Validates the token in the background.
  ///
  /// Priority order:
  ///   1. Server unreachable → stay authenticated offline, do nothing.
  ///   2. Token definitively invalid (401/403) → drop to unauthenticated.
  ///   3. Token valid but expiring within 24 h → silently refresh it.
  ///   4. Token valid and fresh → update device info (secrets auth flag).
  Future<void> _validateInBackground() async {
    try {
      final validationResult = await _authRepo.validateToken();

      if (validationResult == TokenValidationResult.invalid) {
        // Definitively expired or revoked — check if we can reconnect silently
        // using the stored device secret before kicking the user to the login screen.
        final reconnected = await _tryReconnect();
        if (!reconnected && mounted) {
          state = const AuthState.unauthenticated(
            error: 'Session expired. Please log in again.',
          );
        }
        return;
      }

      if (validationResult == TokenValidationResult.valid) {
        // Server is reachable — proactively refresh the token if it's
        // within 24 h of expiry so sessions never silently die on users.
        await _maybeRefreshToken();

        // Fetch updated device info (secrets authorization flag).
        final deviceId = await _secureStorage.getDeviceId() ?? '';
        try {
          final devices = await _authRepo.listDevices();
          final deviceInfo = devices.firstWhere(
            (d) => d['device_id'] == deviceId,
            orElse: () => <String, dynamic>{},
          );
          final isAuth = deviceInfo['is_secrets_authorized'] as bool? ?? false;
          if (mounted) {
            state = AuthState.authenticated(
              deviceId: state.deviceId ?? deviceId,
              deviceName: state.deviceName ?? '',
              serverUrl: state.serverUrl ?? '',
              isSecretsAuthorized: isAuth,
            );
          }
        } catch (_) {
          // listDevices failed — not critical, keep current state
        }
      }
      // If unreachable, do nothing — user continues offline
    } catch (_) {
      // Swallow any unexpected errors — never crash the background validation
    }
  }

  /// Attempt a silent reconnect using the stored device secret.
  ///
  /// Returns true if reconnect succeeded (new token stored and state updated).
  /// Returns false if the device secret is missing or the server rejects it.
  Future<bool> _tryReconnect() async {
    try {
      final deviceName = await _secureStorage.getDeviceName();
      final deviceSecret = await _secureStorage.getDeviceSecret();
      final serverUrl = await _secureStorage.getServerUrl();

      if (deviceName == null || deviceSecret == null || serverUrl == null) {
        return false;
      }

      await _authRepo.reconnectDevice(
        serverUrl: serverUrl,
        deviceName: deviceName,
        deviceSecret: deviceSecret,
      );

      if (mounted) {
        state = AuthState.authenticated(
          deviceId: await _secureStorage.getDeviceId() ?? '',
          deviceName: deviceName,
          serverUrl: serverUrl,
          isSecretsAuthorized: false, // Will be updated in the next validation cycle
        );
      }
      return true;
    } catch (_) {
      return false;
    }
  }

  /// Refresh the JWT if it expires within the next 24 hours.
  ///
  /// Reads the expiry from the JWT payload locally (no network call needed
  /// for the check). Only calls the server if a refresh is actually needed.
  Future<void> _maybeRefreshToken() async {
    try {
      final jwt = await _secureStorage.getJwt();
      if (jwt == null || jwt.isEmpty) return;

      final parts = jwt.split('.');
      if (parts.length != 3) return;

      final payload = _decodeJwtPayload(parts[1]);
      final exp = payload['exp'];
      if (exp == null || exp is! num) return;

      final expiresAt = DateTime.fromMillisecondsSinceEpoch(
        exp.toInt() * 1000,
        isUtc: true,
      );
      final timeLeft = expiresAt.difference(DateTime.now().toUtc());

      // Refresh if less than 24 h remain.
      if (timeLeft.inHours < 24) {
        await _authRepo.refreshToken();
      }
    } catch (_) {
      // JWT decode or refresh failure is non-fatal — the user stays logged in
      // on their current token until it fully expires.
    }
  }

  /// Decode a base64url-encoded JWT payload segment using dart:convert.
  static Map<String, dynamic> _decodeJwtPayload(String base64url) {
    String b64 = base64url.replaceAll('-', '+').replaceAll('_', '/');
    final mod = b64.length % 4;
    if (mod != 0) b64 += '=' * (4 - mod);
    final decoded = utf8.decode(base64Decode(b64));
    return jsonDecode(decoded) as Map<String, dynamic>;
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

  /// Called when the API client detects a 401 mid-session (e.g. an expired
  /// JWT). Mirrors `_validateInBackground`'s invalid-token path: drop to
  /// unauthenticated but keep the stored device credentials so the setup
  /// screen can offer a one-tap reconnect instead of full re-registration.
  void handleUnauthorized() {
    if (!mounted) return;
    if (state.status != AuthStatus.authenticated) return;
    state = const AuthState.unauthenticated(
      error: 'Session expired. Please log in again.',
    );
  }
}

final authProvider = StateNotifierProvider<AuthNotifier, AuthState>((ref) {
  return AuthNotifier(
    ref.watch(authRepositoryProvider),
    ref.watch(secureStorageProvider),
    ref.watch(apiClientProvider),
  );
});
