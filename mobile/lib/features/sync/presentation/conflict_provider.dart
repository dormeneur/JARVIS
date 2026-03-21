import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_provider.dart';

/// Reactive stream of failed (conflicted) mutations.
final failedMutationsProvider = StreamProvider<List<MutationQueueData>>((ref) {
  final db = ref.watch(appDatabaseProvider);
  return db.watchFailedMutations();
});

/// Number of unresolved conflicts (for badge count).
final conflictCountProvider = Provider<int>((ref) {
  final mutations = ref.watch(failedMutationsProvider);
  return mutations.when(
    data: (m) => m.length,
    loading: () => 0,
    error: (_, _) => 0,
  );
});

/// Notifier for conflict resolution actions.
class ConflictNotifier extends StateNotifier<AsyncValue<void>> {
  final Ref _ref;

  ConflictNotifier(this._ref) : super(const AsyncValue.data(null));

  /// Resolve a conflict with the user's final edited content.
  Future<void> resolveConflict(String mutationId, String finalContent) async {
    state = const AsyncValue.loading();
    try {
      final syncRepo = _ref.read(syncRepositoryProvider);
      await syncRepo.resolveConflict(mutationId, finalContent);
      state = const AsyncValue.data(null);
    } catch (e, st) {
      state = AsyncValue.error(e, st);
    }
  }
}

final conflictNotifierProvider =
    StateNotifierProvider<ConflictNotifier, AsyncValue<void>>((ref) {
      return ConflictNotifier(ref);
    });
