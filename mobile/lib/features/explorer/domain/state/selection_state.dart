import 'package:flutter_riverpod/flutter_riverpod.dart';

/// Represents the selection state for the file explorer.
///
/// Tracks which files are currently selected and whether the UI is in selection mode.
class SelectionState {
  /// Set of selected file IDs
  final Set<String> selectedIds;

  /// Whether the UI is in selection mode
  final bool isSelectionMode;

  const SelectionState({
    this.selectedIds = const {},
    this.isSelectionMode = false,
  });

  /// Creates a copy of this state with the given fields replaced
  SelectionState copyWith({
    Set<String>? selectedIds,
    bool? isSelectionMode,
  }) {
    return SelectionState(
      selectedIds: selectedIds ?? this.selectedIds,
      isSelectionMode: isSelectionMode ?? this.isSelectionMode,
    );
  }

  /// Get the count of selected items
  int get selectedCount => selectedIds.length;

  @override
  bool operator ==(Object other) {
    if (identical(this, other)) return true;

    return other is SelectionState &&
        other.selectedIds.length == selectedIds.length &&
        other.selectedIds.containsAll(selectedIds) &&
        other.isSelectionMode == isSelectionMode;
  }

  @override
  int get hashCode => Object.hash(
        selectedIds.length,
        isSelectionMode,
      );
}

/// State notifier for managing file selection state.
///
/// Provides methods for toggling selection, selecting all files, clearing selection,
/// and selecting ranges of files.
class SelectionStateNotifier extends StateNotifier<SelectionState> {
  SelectionStateNotifier() : super(const SelectionState());

  /// Toggle selection for a single file.
  ///
  /// If the file is currently selected, it will be deselected.
  /// If the file is not selected, it will be added to the selection.
  /// Automatically enters selection mode when a file is selected.
  void toggle(String fileId) {
    final newSelectedIds = Set<String>.from(state.selectedIds);

    if (newSelectedIds.contains(fileId)) {
      newSelectedIds.remove(fileId);
    } else {
      newSelectedIds.add(fileId);
    }

    state = state.copyWith(
      selectedIds: newSelectedIds,
      isSelectionMode: newSelectedIds.isNotEmpty,
    );
  }

  /// Select all files in the current view.
  ///
  /// Replaces the current selection with all provided file IDs.
  /// Enters selection mode if any files are provided.
  void selectAll(List<String> allFileIds) {
    state = state.copyWith(
      selectedIds: Set<String>.from(allFileIds),
      isSelectionMode: allFileIds.isNotEmpty,
    );
  }

  /// Clear all selections and exit selection mode.
  void clear() {
    state = const SelectionState(
      selectedIds: {},
      isSelectionMode: false,
    );
  }

  /// Select a range of files between two indices (inclusive).
  ///
  /// The range is normalized so that start and end can be in any order.
  /// All files between min(start, end) and max(start, end) will be added
  /// to the current selection.
  void selectRange(List<String> orderedFileIds, int start, int end) {
    if (orderedFileIds.isEmpty) return;

    // Normalize the range
    final normalizedStart = start < end ? start : end;
    final normalizedEnd = start < end ? end : start;

    // Clamp to valid indices
    final clampedStart = normalizedStart.clamp(0, orderedFileIds.length - 1);
    final clampedEnd = normalizedEnd.clamp(0, orderedFileIds.length - 1);

    // Get the range of file IDs
    final rangeIds = orderedFileIds
        .sublist(clampedStart, clampedEnd + 1)
        .toSet();

    // Add to existing selection
    final newSelectedIds = Set<String>.from(state.selectedIds)..addAll(rangeIds);

    state = state.copyWith(
      selectedIds: newSelectedIds,
      isSelectionMode: true,
    );
  }

  /// Check if a file is currently selected.
  bool isSelected(String fileId) {
    return state.selectedIds.contains(fileId);
  }

  /// Get the count of selected items.
  int get selectedCount => state.selectedCount;
}
