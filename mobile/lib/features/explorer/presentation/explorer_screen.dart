import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/editor/presentation/editor_screen.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_list_screen.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_summary_dialog.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_screen.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';

/// Main file explorer screen — displays files and directories in a list.
class ExplorerScreen extends ConsumerWidget {
  const ExplorerScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final currentDir = ref.watch(currentDirectoryProvider);
    final entriesAsync = ref.watch(directoryEntriesProvider);
    final syncState = ref.watch(syncProvider);
    final conflictCount = ref.watch(conflictCountProvider);
    final theme = Theme.of(context);

    final title = currentDir.isEmpty
        ? 'JARVIS Vault'
        : currentDir.split('/').last;

    return Scaffold(
      appBar: AppBar(
        title: Text(title),
        leading: currentDir.isEmpty
            ? null
            : IconButton(
                icon: const Icon(Icons.arrow_back),
                onPressed: () {
                  final parts = currentDir.split('/');
                  parts.removeLast();
                  ref.read(currentDirectoryProvider.notifier).state = parts
                      .join('/');
                },
              ),
        actions: [
          if (conflictCount > 0)
            Badge.count(
              count: conflictCount,
              backgroundColor: Theme.of(context).colorScheme.error,
              child: IconButton(
                icon: const Icon(Icons.warning_amber_rounded),
                tooltip:
                    '$conflictCount conflict${conflictCount == 1 ? '' : 's'}',
                onPressed: () => Navigator.of(context).push(
                  MaterialPageRoute(builder: (_) => const ConflictListScreen()),
                ),
              ),
            ),
          IconButton(
            icon: const Icon(Icons.settings_outlined),
            onPressed: () => Navigator.of(
              context,
            ).push(MaterialPageRoute(builder: (_) => const SettingsScreen())),
          ),
        ],
      ),
      body: Column(
        children: [
          // Sync status banner
          if (syncState.status == SyncStatus.syncing)
            const LinearProgressIndicator(),
          if (syncState.status == SyncStatus.error)
            MaterialBanner(
              content: Text(syncState.error ?? 'Sync failed'),
              backgroundColor: theme.colorScheme.errorContainer,
              actions: [
                TextButton(
                  onPressed: () => ref.read(syncProvider.notifier).clearError(),
                  child: const Text('Dismiss'),
                ),
              ],
            ),
          // File list
          Expanded(
            child: entriesAsync.when(
              loading: () => const Center(child: CircularProgressIndicator()),
              error: (e, _) => Center(
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    const Icon(Icons.cloud_off, size: 48),
                    const SizedBox(height: 8),
                    Text(
                      'No files yet.\nTap sync to pull from server.',
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyMedium,
                    ),
                  ],
                ),
              ),
              data: (entries) {
                if (entries.isEmpty) {
                  return Center(
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          Icons.folder_open,
                          size: 48,
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                        const SizedBox(height: 8),
                        Text(
                          currentDir.isEmpty
                              ? 'Vault is empty.\nTap sync to pull files.'
                              : 'Empty folder.',
                          textAlign: TextAlign.center,
                          style: theme.textTheme.bodyMedium,
                        ),
                      ],
                    ),
                  );
                }
                return RefreshIndicator(
                  onRefresh: () async {
                    ref.invalidate(directoryEntriesProvider);
                  },
                  child: ListView.builder(
                    itemCount: entries.length,
                    itemBuilder: (context, index) =>
                        _EntryTile(entry: entries[index]),
                  ),
                );
              },
            ),
          ),
        ],
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: syncState.status == SyncStatus.syncing
            ? null
            : () => _triggerSync(context, ref),
        icon: syncState.status == SyncStatus.syncing
            ? const SizedBox(
                height: 18,
                width: 18,
                child: CircularProgressIndicator(strokeWidth: 2),
              )
            : const Icon(Icons.sync),
        label: const Text('Sync'),
      ),
    );
  }

  Future<void> _triggerSync(BuildContext context, WidgetRef ref) async {
    await ref.read(syncProvider.notifier).performSync();
    ref.invalidate(directoryEntriesProvider);

    if (!context.mounted) return;

    final state = ref.read(syncProvider);
    if (state.lastResult != null) {
      showDialog(
        context: context,
        builder: (_) => SyncSummaryDialog(result: state.lastResult!),
      );
    }
  }
}

class _EntryTile extends ConsumerWidget {
  final FileEntry entry;

  const _EntryTile({required this.entry});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final isConflict = entry.name.contains('_conflict_');

    return ListTile(
      leading: Icon(
        entry.isDirectory
            ? Icons.folder
            : isConflict
            ? Icons.warning_amber
            : Icons.description_outlined,
        color: entry.isDirectory
            ? theme.colorScheme.primary
            : isConflict
            ? theme.colorScheme.error
            : null,
      ),
      title: Text(
        entry.name,
        style: isConflict ? TextStyle(color: theme.colorScheme.error) : null,
      ),
      subtitle: entry.isFile
          ? Text(
              '${formatFileSize(entry.sizeBytes)} • ${entry.isSynced ? "Synced" : "Server only"}',
              style: theme.textTheme.bodySmall,
            )
          : null,
      trailing: entry.isDirectory ? const Icon(Icons.chevron_right) : null,
      onTap: () {
        if (entry.isDirectory) {
          ref.read(currentDirectoryProvider.notifier).state = entry.path;
        } else if (entry.localPath != null) {
          Navigator.of(context).push(
            MaterialPageRoute(
              builder: (_) => EditorScreen(filePath: entry.path),
            ),
          );
        }
      },
      onLongPress: entry.isFile && entry.isSynced
          ? () => _showDeleteDialog(context, ref)
          : null,
    );
  }

  Future<void> _showDeleteDialog(BuildContext context, WidgetRef ref) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete File'),
        content: Text(
          'Delete "${entry.name}"?\n\nThis will be synced to the server on next sync.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          TextButton(
            onPressed: () => Navigator.of(context).pop(true),
            style: TextButton.styleFrom(
              foregroundColor: Theme.of(context).colorScheme.error,
            ),
            child: const Text('Delete'),
          ),
        ],
      ),
    );

    if (confirmed == true && context.mounted) {
      try {
        final repo = ref.read(explorerRepositoryProvider);
        await repo.deleteFile(entry.path);

        // Refresh the directory listing
        ref.invalidate(directoryEntriesProvider);

        if (context.mounted) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text('${entry.name} deleted. Will sync on next sync.'),
              duration: const Duration(seconds: 2),
            ),
          );
        }
      } catch (e) {
        if (context.mounted) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text('Failed to delete: $e'),
              backgroundColor: Theme.of(context).colorScheme.error,
            ),
          );
        }
      }
    }
  }
}
