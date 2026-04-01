import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

/// Dialog that lets the user pick a target folder from the vault tree.
///
/// Shows a navigable folder tree starting from root. The user can drill
/// into subfolders and select the target location for move operations.
class FolderPickerDialog extends ConsumerStatefulWidget {
  /// Optional path to exclude from the tree (e.g., the folder being moved).
  final String? excludePath;

  const FolderPickerDialog({super.key, this.excludePath});

  @override
  ConsumerState<FolderPickerDialog> createState() => _FolderPickerDialogState();
}

class _FolderPickerDialogState extends ConsumerState<FolderPickerDialog> {
  String _currentPath = '';
  final List<String> _history = [''];

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final repo = ref.read(explorerRepositoryProvider);

    return AlertDialog(
      title: Row(
        children: [
          const Icon(Icons.drive_file_move_outline),
          const SizedBox(width: 12),
          const Expanded(child: Text('Move to…')),
          IconButton(
            icon: const Icon(Icons.close),
            onPressed: () => Navigator.pop(context),
            visualDensity: VisualDensity.compact,
          ),
        ],
      ),
      titlePadding: const EdgeInsets.fromLTRB(24, 20, 8, 0),
      contentPadding: const EdgeInsets.symmetric(horizontal: 0, vertical: 8),
      content: SizedBox(
        width: double.maxFinite,
        height: 400,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Current path display
            Container(
              width: double.infinity,
              padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
              color: theme.colorScheme.surfaceContainerLow,
              child: Row(
                children: [
                  Icon(Icons.folder_open, size: 18, color: theme.colorScheme.primary),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Text(
                      _currentPath.isEmpty ? '/ (Vault root)' : '/$_currentPath',
                      style: theme.textTheme.bodySmall?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              ),
            ),
            // Back button
            if (_history.length > 1)
              ListTile(
                leading: const Icon(Icons.arrow_upward),
                title: const Text('Go up'),
                dense: true,
                onTap: () {
                  setState(() {
                    _history.removeLast();
                    _currentPath = _history.last;
                  });
                },
              ),
            // Folder list
            Expanded(
              child: FutureBuilder<List<FileEntry>>(
                future: repo.listDirectory(_currentPath),
                builder: (context, snapshot) {
                  if (snapshot.connectionState == ConnectionState.waiting) {
                    return const Center(child: CircularProgressIndicator());
                  }

                  final entries = snapshot.data ?? [];
                  final folders = entries
                      .where((e) => e.isDirectory)
                      .where((e) => e.path != widget.excludePath)
                      .toList();

                  if (folders.isEmpty) {
                    return Center(
                      child: Text(
                        'No subfolders',
                        style: theme.textTheme.bodyMedium?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      ),
                    );
                  }

                  return ListView.builder(
                    itemCount: folders.length,
                    itemBuilder: (context, index) {
                      final folder = folders[index];
                      return ListTile(
                        leading: Icon(Icons.folder,
                            color: theme.colorScheme.primary),
                        title: Text(folder.name),
                        trailing: const Icon(Icons.chevron_right, size: 20),
                        dense: true,
                        onTap: () {
                          setState(() {
                            _currentPath = folder.path;
                            _history.add(folder.path);
                          });
                        },
                      );
                    },
                  );
                },
              ),
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Cancel'),
        ),
        FilledButton.icon(
          icon: const Icon(Icons.check, size: 18),
          label: const Text('Move here'),
          onPressed: () => Navigator.pop(context, _currentPath),
        ),
      ],
    );
  }
}
