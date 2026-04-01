import 'dart:async';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/auth/data/auth_repository.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';

enum ServerConnectionState { checking, online, offline }

class ServerConnectionNotifier extends StateNotifier<ServerConnectionState> {
  final AuthRepository _authRepo;
  final SecureStorage _secureStorage;
  Timer? _timer;

  ServerConnectionNotifier(this._authRepo, this._secureStorage)
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
  );
});
