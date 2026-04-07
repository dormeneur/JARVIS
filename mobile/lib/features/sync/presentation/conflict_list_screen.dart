import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_detail_screen.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_provider.dart';

/// Lists all failed (conflict) mutations. Each tile navigates to the
/// [ConflictDetailScreen] where the user can resolve the conflict.
class ConflictListScreen extends ConsumerWidget {
  const ConflictListScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mutationsAsync = ref.watch(failedMutationsProvider);
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(title: const Text('Conflicts'), centerTitle: false),
      body: mutationsAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Text(
              'Failed to load conflicts:\n$e',
              textAlign: TextAlign.center,
              style: TextStyle(color: theme.colorScheme.error),
            ),
          ),
        ),
        data: (mutations) {
          if (mutations.isEmpty) {
            return Center(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Icon(
                    Icons.check_circle_outline,
                    size: 56,
                    color: theme.colorScheme.primary,
                  ),
                  const SizedBox(height: 12),
                  Text(
                    'No conflicts.\nEverything is in sync.',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodyLarge,
                  ),
                ],
              ),
            );
          }

          return ListView.separated(
            itemCount: mutations.length,
            separatorBuilder: (_, _) => const Divider(height: 1),
            itemBuilder: (context, index) =>
                _ConflictTile(mutation: mutations[index]),
          );
        },
      ),
    );
  }
}

class _ConflictTile extends ConsumerWidget {
  final MutationQueueData mutation;

  const _ConflictTile({required this.mutation});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final fileName = mutation.path.split('/').last;

    return ListTile(
      leading: Icon(
        Icons.warning_amber_rounded,
        color: theme.colorScheme.error,
      ),
      title: Text(
        fileName,
        style: const TextStyle(fontWeight: FontWeight.w600),
      ),
      subtitle: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            mutation.path,
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
          const SizedBox(height: 2),
          _chip(
            context,
            label: 'CONFLICT',
            color: theme.colorScheme.errorContainer,
            textColor: theme.colorScheme.onErrorContainer,
          ),
        ],
      ),
      trailing: IconButton(
        icon: Icon(Icons.delete_outline, color: theme.colorScheme.error),
        tooltip: 'Delete from all devices',
        onPressed: () => _confirmDelete(context, ref),
      ),
      isThreeLine: true,
      onTap: () {
        Navigator.of(context).push(
          MaterialPageRoute(
            builder: (_) => ConflictDetailScreen(mutation: mutation),
          ),
        );
      },
    );
  }

  void _confirmDelete(BuildContext context, WidgetRef ref) async {
    final fileName = mutation.path.split('/').last;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Delete from all devices?'),
        content: Text(
          'Permanently delete "$fileName" from the server '
          'and this device to clear this conflict.\n\nThis cannot be undone.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(ctx, true),
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(ctx).colorScheme.error,
            ),
            child: const Text('Delete Everywhere'),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) return;

    final notifier = ref.read(conflictNotifierProvider.notifier);
    await notifier.deleteConflict(mutation.id);
    ref.invalidate(directoryEntriesProvider);

    if (!context.mounted) return;
    ScaffoldMessenger.of(context).showSnackBar(
      const SnackBar(
        content: Text('File deleted from all devices.'),
        duration: Duration(seconds: 2),
      ),
    );
  }

  Widget _chip(
    BuildContext context, {
    required String label,
    required Color color,
    required Color textColor,
  }) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
      decoration: BoxDecoration(
        color: color,
        borderRadius: BorderRadius.circular(4),
      ),
      child: Text(
        label,
        style: TextStyle(
          fontSize: 11,
          color: textColor,
          fontWeight: FontWeight.w500,
        ),
      ),
    );
  }
}
