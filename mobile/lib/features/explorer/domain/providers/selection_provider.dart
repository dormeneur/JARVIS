import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../state/selection_state.dart';

/// Provider for the selection state notifier.
///
/// This provider manages the multi-select state for the file explorer,
/// including which files are selected and whether the UI is in selection mode.
///
/// Usage:
/// ```dart
/// // Watch the entire selection state
/// final selectionState = ref.watch(selectionStateProvider);
///
/// // Watch only if a specific file is selected (optimized)
/// final isSelected = ref.watch(
///   selectionStateProvider.select((s) => s.selectedIds.contains(fileId))
/// );
///
/// // Access the notifier to modify state
/// ref.read(selectionStateProvider.notifier).toggle(fileId);
/// ```
final selectionStateProvider =
    StateNotifierProvider<SelectionStateNotifier, SelectionState>((ref) {
  return SelectionStateNotifier();
});
