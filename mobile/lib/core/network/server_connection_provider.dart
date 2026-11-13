import 'dart:async';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/autofill/autofill_repository.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/auth/data/auth_repository.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';

enum ServerConnectionState { checking, online, offline }

class ServerConnectionNotifier extends StateNotifier<ServerConnectionState> {
  final AuthRepository _authRepo;
  final SecureStorage _secureStorage;
  Timer? _timer;

  final Future<void> Function()? _onOnline;

  ServerConnectionNotifier(this._authRepo, this._secureStorage, [this._onOnline])
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
      final isHealthy = await _authRepo.checkServerHealth(serverUrl);
      if (mounted) {
        state = isHealthy ? ServerConnectionState.online : ServerConnectionState.offline;
      }
      // Whatever the phone queued while the PC was away goes now; a failure just waits for the next poll.
      if (isHealthy) await _onOnline?.call().catchError((_) {});
    } catch (_) {
      if (mounted) {
        state = ServerConnectionState.offline;
      }
    }
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }
}

final serverConnectionProvider = StateNotifierProvider<ServerConnectionNotifier, ServerConnectionState>((ref) {
  return ServerConnectionNotifier(
    ref.watch(authRepositoryProvider),
    ref.watch(secureStorageProvider),
    () => ref.read(autofillRepositoryProvider).flushPending(),
  );
});
