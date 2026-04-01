import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/explorer/domain/providers/clipboard_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/providers/file_operation_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/providers/selection_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/state/clipboard_state.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';

/// App bar displayed when the file explorer is in selection mode.
///
/// Shows the number of selected items and provides action buttons for
/// common operations: Select All, Cut, Copy, Delete, and Paste.
/// A close button allows the user to exit selection mode.
class SelectionAppBar extends ConsumerWidget implements PreferredSizeWidget {
  const SelectionAppBar({super.key});

  @override
  Size get preferredSize => const Size.fromHeight(kToolbarHeight);

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selectionState = ref.watch(selectionStateProvider);
    final clipboardState = ref.watch(clipboardStateProvider);
    final theme = Theme.of(context);

    final selectedCount = selectionState.selectedCount;

    return AppBar(
      backgroundColor: theme.colorScheme.primaryContainer,
      foregroundColor: theme.colorScheme.onPrimaryContainer,
      leading: IconButton(
        icon: const Icon(Icons.close),
        tooltip: 'Exit selection',
        onPressed: () {
          ref.read(selectionStateProvider.notifier).clear();
        },
      ),
      title: Text(
        '$selectedCount selected',
        style: theme.textTheme.titleMedium?.copyWith(
          color: theme.colorScheme.onPrimaryContainer,
          fontWeight: FontWeight.w600,
        ),
      ),
      actions: [
        // Select All
        IconButton(
          icon: const Icon(Icons.select_all),
          tooltip: 'Select all',
          onPressed: () {
            final entries = ref.read(directoryEntriesProvider).valueOrNull ?? [];
            final allIds = entries.map((e) => e.path).toList();
            ref.read(selectionStateProvider.notifier).selectAll(allIds);
          },
        ),
        // Cut
        IconButton(
          icon: const Icon(Icons.content_cut),
          tooltip: 'Cut',
          onPressed: selectedCount == 0
              ? null
              : () {
                  final selectedIds =
                      ref.read(selectionStateProvider).selectedIds.toList();
                  ref.read(clipboardStateProvider.notifier).cut(selectedIds);
                  ref.read(selectionStateProvider.notifier).clear();
                  if (context.mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(
                      SnackBar(
                        content: Text(
                          '$selectedCount item${selectedCount == 1 ? '' : 's'} cut to clipboard',
                        ),
                        duration: const Duration(seconds: 2),
                      ),
                    );
                  }
                },
        ),
        // Copy
        IconButton(
          icon: const Icon(Icons.copy),
          tooltip: 'Copy',
          onPressed: selectedCount == 0
              ? null
              : () {
                  final selectedIds =
                      ref.read(selectionStateProvider).selectedIds.toList();
                  ref.read(clipboardStateProvider.notifier).copy(selectedIds);
                  ref.read(selectionStateProvider.notifier).clear();
                  if (context.mounted) {
                    ScaffoldMessenger.of(context).showSnackBar(
                      SnackBar(
                        content: Text(
                          '$selectedCount item${selectedCount == 1 ? '' : 's'} copied to clipboard',
                        ),
                        duration: const Duration(seconds: 2),
                      ),
                    );
                  }
                },
        ),
        // Delete
        IconButton(
          icon: Icon(Icons.delete_outline, color: theme.colorScheme.error),
          tooltip: 'Delete',
          onPressed: selectedCount == 0
              ? null
              : () => _showDeleteConfirmation(context, ref, selectedCount),
        ),
        // Paste (only show if clipboard has items)
        if (clipboardState.isNotEmpty)
          IconButton(
            icon: const Icon(Icons.paste),
            tooltip: clipboardState.operation == ClipboardOperation.cut
                ? 'Paste (move)'
                : 'Paste (copy)',
            onPressed: () => _executePaste(context, ref),
          ),
      ],
    );
  }

  Future<void> _showDeleteConfirmation(
    BuildContext context,
    WidgetRef ref,
    int count,
  ) async {
    final selectedIds = ref.read(selectionStateProvider).selectedIds.toList();
    final repo = ref.read(explorerRepositoryProvider);
    
    int totalDescendants = 0;
    for (final id in selectedIds) {
      final entry = await repo.getEntry(id);
      if (entry != null && entry.isDirectory) {
        totalDescendants += await repo.getDescendantCount(entry.path);
      }
    }
    
    final descendantText = totalDescendants > 0 
        ? '\n\nThis will also delete $totalDescendants nested item${totalDescendants == 1 ? '' : 's'}.'
        : '';
        
    if (!context.mounted) return;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Delete Files'),
        content: Text(
          'Delete $count selected item${count == 1 ? '' : 's'}?$descendantText\n\n'
          'This action will be synced to the server.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(ctx).colorScheme.error,
            ),
            child: const Text('Delete'),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) return;

    final service = ref.read(fileOperationServiceProvider);
    final result = await service.deleteFiles(selectedIds);

    // Clear selection after operation
    ref.read(selectionStateProvider.notifier).clear();

    // Refresh the directory listing
    ref.invalidate(directoryEntriesProvider);

    if (!context.mounted) return;

    if (result.isSuccess) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            '${result.successfulIds.length} item${result.successfulIds.length == 1 ? '' : 's'} deleted',
          ),
          duration: const Duration(seconds: 2),
        ),
      );
    } else if (result.isPartialSuccess) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(result.message),
          backgroundColor: Theme.of(context).colorScheme.errorContainer,
          duration: const Duration(seconds: 3),
        ),
      );
    } else {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Delete failed: ${result.errors.first.message}'),
          backgroundColor: Theme.of(context).colorScheme.error,
        ),
      );
    }
  }

  Future<void> _executePaste(BuildContext context, WidgetRef ref) async {
    final currentDir = ref.read(currentDirectoryProvider);
    final clipboardNotifier = ref.read(clipboardStateProvider.notifier);
    final service = ref.read(fileOperationServiceProvider);
    final clipboardState = ref.read(clipboardStateProvider);

    final itemCount = clipboardState.fileIds.length;
    final opName =
        clipboardState.operation == ClipboardOperation.cut ? 'Moving' : 'Copying';

    final repo = ref.read(explorerRepositoryProvider);
    int totalDescendants = 0;
    for (final id in clipboardState.fileIds) {
      final entry = await repo.getEntry(id);
      if (entry != null && entry.isDirectory) {
        totalDescendants += await repo.getDescendantCount(entry.path);
      }
    }
    
    final descendantText = totalDescendants > 0 
        ? ' (and $totalDescendants nested file${totalDescendants == 1 ? '' : 's'})'
        : '';

    // Show a brief indicator
    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('$opName $itemCount item${itemCount == 1 ? '' : 's'}$descendantText…'),
          duration: const Duration(seconds: 1),
        ),
      );
    }

    final result = await clipboardNotifier.paste(currentDir, service);

    // Refresh the directory listing
    ref.invalidate(directoryEntriesProvider);

    // Clear selection
    ref.read(selectionStateProvider.notifier).clear();

    if (!context.mounted) return;

    if (result.isSuccess) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            '${result.successfulIds.length} item${result.successfulIds.length == 1 ? '' : 's'} pasted',
          ),
          duration: const Duration(seconds: 2),
        ),
      );
    } else if (result.isPartialSuccess) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(result.message),
          backgroundColor: Theme.of(context).colorScheme.errorContainer,
          duration: const Duration(seconds: 3),
        ),
      );
    } else {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Paste failed: ${result.errors.first.message}'),
          backgroundColor: Theme.of(context).colorScheme.error,
        ),
      );
    }
  }
}
