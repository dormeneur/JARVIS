import 'dart:math';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/explorer/domain/state/selection_state.dart';

/// Property-based tests for SelectionState and SelectionStateNotifier.
///
/// These tests verify the correctness properties of the selection system
/// using randomly generated inputs to ensure comprehensive coverage.
///
/// Each property test runs a minimum of 100 iterations with randomly generated
/// inputs to ensure universal correctness.
void main() {
  group('Property-Based Tests: Selection State', () {
    late SelectionStateNotifier notifier;

    setUp(() {
      notifier = SelectionStateNotifier();
    });

    // Feature: advanced-file-manager, Property 1: Selection Toggle Idempotence
    // Validates: Requirements 1.1
    test('Property 1: Toggling a file twice returns to original state', () {
      const iterations = 100;
      final random = Random(1001);

      for (int i = 0; i < iterations; i++) {
        // Reset state
        notifier = SelectionStateNotifier();

        // Generate random file IDs and pre-populate some selections
        final allIds = _generateRandomFileIds(random, count: 20);
        final preSelectedCount = random.nextInt(10);
        for (int j = 0; j < preSelectedCount && j < allIds.length; j++) {
          notifier.toggle(allIds[j]);
        }

        // Capture state before
        final stateBefore = notifier.state;

        // Pick a random file ID to toggle
        final toggleId = allIds[random.nextInt(allIds.length)];

        // Toggle twice
        notifier.toggle(toggleId);
        notifier.toggle(toggleId);

        // State should be identical to before
        expect(
          notifier.state.selectedIds,
          stateBefore.selectedIds,
          reason:
              'Iteration $i: Toggling "$toggleId" twice should return to original state',
        );
      }
    });

    // Feature: advanced-file-manager, Property 2: Multi-Select Accumulation
    // Validates: Requirements 2.1, 2.2
    test('Property 2: Multiple toggles accumulate correctly', () {
      const iterations = 100;
      final random = Random(1002);

      for (int i = 0; i < iterations; i++) {
        notifier = SelectionStateNotifier();

        // Generate random file IDs
        final allIds = _generateRandomFileIds(random, count: 15);
        
        // Select a random subset
        final toSelect = <String>{};
        final selectCount = random.nextInt(allIds.length) + 1;
        for (int j = 0; j < selectCount; j++) {
          final id = allIds[random.nextInt(allIds.length)];
          toSelect.add(id);
        }

        // Toggle each unique ID once
        for (final id in toSelect) {
          notifier.toggle(id);
        }

        // Verify all toggled items are selected
        for (final id in toSelect) {
          expect(
            notifier.isSelected(id),
            isTrue,
            reason: 'Iteration $i: "$id" should be selected after toggle',
          );
        }

        // Verify unselected items remain unselected
        for (final id in allIds) {
          if (!toSelect.contains(id)) {
            expect(
              notifier.isSelected(id),
              isFalse,
              reason:
                  'Iteration $i: "$id" should NOT be selected (never toggled)',
            );
          }
        }

        // Verify selectedCount matches
        expect(
          notifier.selectedCount,
          toSelect.length,
          reason:
              'Iteration $i: selectedCount should be ${toSelect.length}',
        );
      }
    });

    // Feature: advanced-file-manager, Property 3: Select All Completeness
    // Validates: Requirements 2.3
    test('Property 3: selectAll includes all provided IDs', () {
      const iterations = 100;
      final random = Random(1003);

      for (int i = 0; i < iterations; i++) {
        notifier = SelectionStateNotifier();

        // Generate random file ID list
        final allIds = _generateRandomFileIds(random, count: random.nextInt(50) + 1);

        // Select all
        notifier.selectAll(allIds);

        // Verify all IDs are selected
        for (final id in allIds) {
          expect(
            notifier.isSelected(id),
            isTrue,
            reason: 'Iteration $i: "$id" should be selected after selectAll',
          );
        }

        // Verify count
        final uniqueIds = allIds.toSet();
        expect(
          notifier.selectedCount,
          uniqueIds.length,
          reason:
              'Iteration $i: selectedCount (${notifier.selectedCount}) should equal unique ID count (${uniqueIds.length})',
        );

        // Verify isSelectionMode is true (if any items)
        if (allIds.isNotEmpty) {
          expect(
            notifier.state.isSelectionMode,
            isTrue,
            reason: 'Iteration $i: Should be in selection mode after selectAll',
          );
        }
      }
    });

    // Feature: advanced-file-manager, Property 4: Deselect All Clears Selection
    // Validates: Requirements 3.1
    test('Property 4: clear() removes all selections', () {
      const iterations = 100;
      final random = Random(1004);

      for (int i = 0; i < iterations; i++) {
        notifier = SelectionStateNotifier();

        // Randomly populate selections
        final allIds = _generateRandomFileIds(random, count: 20);
        final selectCount = random.nextInt(allIds.length) + 1;
        for (int j = 0; j < selectCount; j++) {
          notifier.toggle(allIds[j]);
        }

        // Verify something is selected
        expect(notifier.state.selectedIds.isNotEmpty, isTrue,
            reason: 'Pre-condition: should have selections');

        // Clear
        notifier.clear();

        // Verify all cleared
        expect(
          notifier.state.selectedIds.isEmpty,
          isTrue,
          reason: 'Iteration $i: selectedIds should be empty after clear',
        );
        expect(
          notifier.state.isSelectionMode,
          isFalse,
          reason: 'Iteration $i: isSelectionMode should be false after clear',
        );
        expect(
          notifier.selectedCount,
          0,
          reason: 'Iteration $i: selectedCount should be 0 after clear',
        );

        // Verify no ID is selected
        for (final id in allIds) {
          expect(
            notifier.isSelected(id),
            isFalse,
            reason:
                'Iteration $i: "$id" should not be selected after clear',
          );
        }
      }
    });

    // Feature: advanced-file-manager, Property 5: Range Selection Inclusivity
    // Validates: Requirements 3.3
    test('Property 5: selectRange includes all items between start and end', () {
      const iterations = 100;
      final random = Random(1005);

      for (int i = 0; i < iterations; i++) {
        notifier = SelectionStateNotifier();

        // Generate ordered file ID list
        final listSize = random.nextInt(30) + 5; // 5-34 items
        final orderedIds = _generateRandomFileIds(random, count: listSize);

        // Generate random start and end indices
        final start = random.nextInt(listSize);
        final end = random.nextInt(listSize);

        // Pre-select some IDs
        final preSelectCount = random.nextInt(5);
        final preSelected = <String>{};
        for (int j = 0; j < preSelectCount && j < orderedIds.length; j++) {
          notifier.toggle(orderedIds[j]);
          preSelected.add(orderedIds[j]);
        }

        // Perform range selection
        notifier.selectRange(orderedIds, start, end);

        // Calculate expected range
        final normalizedStart = start < end ? start : end;
        final normalizedEnd = start < end ? end : start;
        final clampedStart = normalizedStart.clamp(0, orderedIds.length - 1);
        final clampedEnd = normalizedEnd.clamp(0, orderedIds.length - 1);

        // Verify all items in range are selected
        for (int j = clampedStart; j <= clampedEnd; j++) {
          expect(
            notifier.isSelected(orderedIds[j]),
            isTrue,
            reason:
                'Iteration $i: Item at index $j ("${orderedIds[j]}") should be selected in range [$clampedStart, $clampedEnd]',
          );
        }

        // Verify pre-selected items are still selected
        for (final id in preSelected) {
          expect(
            notifier.isSelected(id),
            isTrue,
            reason:
                'Iteration $i: Pre-selected "$id" should remain selected',
          );
        }

        // Verify selection mode is active
        expect(
          notifier.state.isSelectionMode,
          isTrue,
          reason: 'Iteration $i: Should be in selection mode after range select',
        );
      }
    });

    test('Property 1: Toggle enters selection mode on first select, exits on last deselect', () {
      const iterations = 100;
      final random = Random(1006);

      for (int i = 0; i < iterations; i++) {
        notifier = SelectionStateNotifier();

        // Initially not in selection mode
        expect(notifier.state.isSelectionMode, isFalse,
            reason: 'Should start not in selection mode');

        final id = 'file_${random.nextInt(1000)}';

        // Toggle on - should enter selection mode
        notifier.toggle(id);
        expect(notifier.state.isSelectionMode, isTrue,
            reason: 'Should enter selection mode after selecting');

        // Toggle off - should exit selection mode (no items left)
        notifier.toggle(id);
        expect(notifier.state.isSelectionMode, isFalse,
            reason: 'Should exit selection mode when all deselected');
      }
    });

    test('Property 3: selectAll with empty list keeps selection mode off', () {
      const iterations = 50;

      for (int i = 0; i < iterations; i++) {
        notifier = SelectionStateNotifier();
        notifier.selectAll([]);

        expect(notifier.state.isSelectionMode, isFalse,
            reason: 'Selecting empty list should not enter selection mode');
        expect(notifier.selectedCount, 0,
            reason: 'No items should be selected');
      }
    });

    test('Property 5: selectRange with empty list is a no-op', () {
      const iterations = 50;
      final random = Random(1007);

      for (int i = 0; i < iterations; i++) {
        notifier = SelectionStateNotifier();

        // Pre-select something
        notifier.toggle('existing_file');
        final stateBefore = notifier.state;

        notifier.selectRange([], random.nextInt(10), random.nextInt(10));

        // State should not change
        expect(notifier.state.selectedIds, stateBefore.selectedIds,
            reason: 'Range select on empty list should not change state');
      }
    });
  });
}

// =============================================================================
// Helpers
// =============================================================================

/// Generate a list of random unique file IDs
List<String> _generateRandomFileIds(Random random, {required int count}) {
  final ids = <String>[];
  for (int i = 0; i < count; i++) {
    ids.add('file_${random.nextInt(100000)}_$i');
  }
  return ids;
}
