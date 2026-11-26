import 'dart:async';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/autofill/autofill_repository.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/auth/data/auth_repository.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/network/server_routes.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';

enum ServerConnectionState { checking, online, offline }

class ServerConnectionNotifier extends StateNotifier<ServerConnectionState> {
  final AuthRepository _authRepo;
  final SecureStorage _secureStorage;
  Timer? _timer;

  final Future<void> Function()? _onOnline;
  final ApiClient? _api;
  final void Function(String? route)? _onRoute;

  ServerConnectionNotifier(this._authRepo, this._secureStorage, [this._onOnline, this._api, this._onRoute])
      : super(ServerConnectionState.checking) {
    _startPolling();
  }

  void _startPolling() {
    // Initial check
    checkNow();
    // Poll every 15 seconds for connection status changes
    _timer = Timer.periodic(const Duration(seconds: 15), (_) => checkNow());
  }

  Future<void> checkNow() async {
    // Show 'checking' loading state only if we were offline before to prevent UI flicker
    if (state == ServerConnectionState.offline) {
      state = ServerConnectionState.checking;
    }

    final serverUrl = await _secureStorage.getServerUrl();
    if (serverUrl == null || serverUrl.isEmpty) {
      state = ServerConnectionState.offline;
      return;
    }

    try {
      // Every way to the PC at once (Tailscale, Wi-Fi, USB); the first in order that answers wins.
      final candidates = ServerRoutes.candidates(serverUrl, await _secureStorage.getRoutes());
      final working = await ServerRoutes.firstAnswering(candidates, _authRepo.checkServerHealth);
      final isHealthy = working != null;
      if (working != null) {
        if (working != ServerRoutes.normalise(serverUrl)) await _secureStorage.setServerUrl(working);
        _api?.setBaseUrl(working); // every request follows the way that works now
      }
      _onRoute?.call(working == null ? null : ServerRoutes.label(working));
      if (mounted) {
        state = isHealthy ? ServerConnectionState.online : ServerConnectionState.offline;
      }
      if (working != null) await _learnRoutes(working).catchError((_) {});
      // Whatever the phone queued while the PC was away goes now; a failure just waits for the next poll.
      if (isHealthy) await _onOnline?.call().catchError((_) {});
    } catch (_) {
      if (mounted) {
        state = ServerConnectionState.offline;
      }
    }
  }

  /// Asks the server where else it answers, so the phone can switch when this way goes (USB unplugged).
  Future<void> _learnRoutes(String working) async {
    final api = _api;
    if (api == null) return;
    final response = await api.dio.get('$working/server/routes'); // absolute: the base may not be set yet
    final found = ((response.data as Map?)?['routes'] as List?)?.whereType<String>() ?? const <String>[];
    await _secureStorage.setRoutes(ServerRoutes.remember(await _secureStorage.getRoutes(), [working, ...found]));
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }
}

/// How the phone reaches the PC right now ("Tailscale", "Home Wi-Fi", "USB"), or null when it can't.
final serverRouteProvider = StateProvider<String?>((ref) => null);

final serverConnectionProvider = StateNotifierProvider<ServerConnectionNotifier, ServerConnectionState>((ref) {
  return ServerConnectionNotifier(
    ref.watch(authRepositoryProvider),
    ref.watch(secureStorageProvider),
    () => ref.read(autofillRepositoryProvider).flushPending(),
    ref.read(apiClientProvider),
    (route) => ref.read(serverRouteProvider.notifier).state = route,
  );
});
