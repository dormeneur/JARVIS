import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/sync/data/sync_repository.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_provider.dart';

/// Reactive stream of all failed (conflict) mutations.
final failedMutationsProvider = StreamProvider<List<MutationQueueData>>((ref) {
  final db = ref.watch(appDatabaseProvider);
  return db.watchFailedMutations();
});

/// Count of active conflicts for badge display.
final conflictCountProvider = Provider<int>((ref) {
  return ref
      .watch(failedMutationsProvider)
      .maybeWhen(data: (list) => list.length, orElse: () => 0);
});

// ---------------------------------------------------------------------------
// ConflictNotifier
// ---------------------------------------------------------------------------

/// State for [ConflictNotifier]: tracks in-progress resolution actions.
class ConflictResolutionState {
  final bool isLoading;
  final String? error;

  const ConflictResolutionState({this.isLoading = false, this.error});

  ConflictResolutionState copyWith({bool? isLoading, String? error}) {
    return ConflictResolutionState(
      isLoading: isLoading ?? this.isLoading,
      error: error,
    );
  }
}

/// Notifier that exposes resolution actions. All actions go through
/// [SyncRepository] — no business logic lives in the UI.
class ConflictNotifier extends StateNotifier<ConflictResolutionState> {
  final SyncRepository _syncRepo;

  ConflictNotifier(this._syncRepo) : super(const ConflictResolutionState());

  Future<void> resolveKeepLocal(String mutationId) async {
    state = state.copyWith(isLoading: true, error: null);
    try {
      await _syncRepo.resolveKeepLocal(mutationId);
      state = const ConflictResolutionState();
    } catch (e) {
      state = ConflictResolutionState(error: e.toString());
    }
  }

  Future<void> resolveAcceptRemote(String mutationId) async {
    state = state.copyWith(isLoading: true, error: null);
    try {
      await _syncRepo.resolveAcceptRemote(mutationId);
      state = const ConflictResolutionState();
    } catch (e) {
      state = ConflictResolutionState(error: e.toString());
    }
  }

  Future<void> resolveManualEdit(
    String mutationId,
    String mergedContent,
  ) async {
    state = state.copyWith(isLoading: true, error: null);
    try {
      await _syncRepo.resolveManualEdit(mutationId, mergedContent);
      state = const ConflictResolutionState();
    } catch (e) {
      state = ConflictResolutionState(error: e.toString());
    }
  }
}

final conflictNotifierProvider =
    StateNotifierProvider<ConflictNotifier, ConflictResolutionState>((ref) {
      return ConflictNotifier(ref.watch(syncRepositoryProvider));
    });
