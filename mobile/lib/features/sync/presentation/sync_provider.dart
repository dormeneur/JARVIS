import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/sync/data/sync_repository.dart';
import 'package:jarvis_mobile/shared/models/sync_result.dart';

final syncRepositoryProvider = Provider<SyncRepository>((ref) {
  return SyncRepository(
    apiClient: ref.watch(apiClientProvider),
    explorerRepo: ref.watch(explorerRepositoryProvider),
    db: ref.watch(appDatabaseProvider),
  );
});

enum SyncStatus { idle, syncing, complete, error }

class SyncState {
  final SyncStatus status;
  final SyncResult? lastResult;
  final String? error;

  const SyncState({this.status = SyncStatus.idle, this.lastResult, this.error});

  SyncState copyWith({
    SyncStatus? status,
    SyncResult? lastResult,
    String? error,
  }) {
    return SyncState(
      status: status ?? this.status,
      lastResult: lastResult ?? this.lastResult,
      error: error,
    );
  }
}

class SyncNotifier extends StateNotifier<SyncState> {
  final SyncRepository _syncRepo;

  SyncNotifier(this._syncRepo) : super(const SyncState());

  Future<void> performSync() async {
    state = state.copyWith(status: SyncStatus.syncing, error: null);

    try {
      final result = await _syncRepo.performSync();
      state = SyncState(status: SyncStatus.complete, lastResult: result);
    } catch (e) {
      state = SyncState(status: SyncStatus.error, error: e.toString());
    }
  }

  void clearError() {
    state = const SyncState(status: SyncStatus.idle);
  }
}

final syncProvider = StateNotifierProvider<SyncNotifier, SyncState>((ref) {
  return SyncNotifier(ref.watch(syncRepositoryProvider));
});
