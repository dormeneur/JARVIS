import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/explorer/domain/providers/clipboard_provider.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';

/// Bottom sheet context menu for a single file/folder.
///
/// Shows relevant actions: Open, Rename, Copy, Cut, Delete, Move to..., Select.
/// Triggered by long-press on a file tile in normal mode.
class FileContextMenu extends ConsumerWidget {
  final FileEntry entry;

  const FileContextMenu({super.key, required this.entry});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final clipboardState = ref.watch(clipboardStateProvider);

    return SafeArea(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Drag handle
          Padding(
            padding: const EdgeInsets.only(top: 12, bottom: 4),
            child: Container(
              width: 36,
              height: 4,
              decoration: BoxDecoration(
                color: theme.colorScheme.onSurfaceVariant.withValues(alpha: 0.3),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          // File info header
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
            child: Row(
              children: [
                Icon(
                  entry.isDirectory ? Icons.folder : Icons.description_outlined,
                  size: 40,
                  color: entry.isDirectory
                      ? theme.colorScheme.primary
                      : theme.colorScheme.onSurfaceVariant,
                ),
                const SizedBox(width: 16),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        entry.name,
                        style: theme.textTheme.titleSmall?.copyWith(
                          fontWeight: FontWeight.w600,
                        ),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                      ),
                      const SizedBox(height: 2),
                      Text(
                        _buildSubtitle(),
                        style: theme.textTheme.bodySmall?.copyWith(
                          color: theme.colorScheme.onSurfaceVariant,
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
          ),
          const Divider(height: 1),
          // Actions
          if (entry.isFile)
            _ActionTile(
              icon: Icons.edit_outlined,
              label: 'Open',
              onTap: () {
                Navigator.pop(context, 'open');
              },
            ),
          if (entry.isFile && entry.name.toLowerCase().endsWith('.pdf'))
            _ActionTile(
              icon: Icons.compress,
              label: 'Extract Text to MD',
              onTap: () {
                Navigator.pop(context, 'extract-pdf');
              },
            ),
          _ActionTile(
            icon: Icons.drive_file_rename_outline,
            label: 'Rename',
            onTap: () {
              Navigator.pop(context, 'rename');
            },
          ),
          _ActionTile(
            icon: Icons.content_copy_outlined,
            label: 'Copy',
            onTap: () {
              ref.read(clipboardStateProvider.notifier).copy([entry.path]);
              Navigator.pop(context);
              _showSnackBar(context, 'Copied to clipboard');
            },
          ),
          _ActionTile(
            icon: Icons.content_cut,
            label: 'Cut',
            onTap: () {
              ref.read(clipboardStateProvider.notifier).cut([entry.path]);
              Navigator.pop(context);
              _showSnackBar(context, 'Cut to clipboard');
            },
          ),
          _ActionTile(
            icon: Icons.drive_file_move_outline,
            label: 'Move to…',
            onTap: () {
              // Popping this sheet unmounts its BuildContext, so the
              // follow-up folder picker + confirm dialog run in the caller
              // (explorer_screen's stable context), not here.
              Navigator.pop(context, 'move');
            },
          ),
          if (clipboardState.isNotEmpty)
            _ActionTile(
              icon: Icons.paste,
              label:
                  'Paste here (${clipboardState.fileIds.length} item${clipboardState.fileIds.length == 1 ? '' : 's'})',
              onTap: () {
                Navigator.pop(context, 'paste');
              },
            ),
          const Divider(height: 1),
          _ActionTile(
            icon: Icons.check_box_outlined,
            label: 'Select',
            onTap: () {
              Navigator.pop(context, 'select');
            },
          ),
          _ActionTile(
            icon: Icons.delete_outline,
            label: 'Delete',
            color: theme.colorScheme.error,
            onTap: () {
              Navigator.pop(context, 'delete');
            },
          ),
          const SizedBox(height: 8),
        ],
      ),
    );
  }

  String _buildSubtitle() {
    final parts = <String>[];
    if (entry.isDirectory) {
      parts.add('Folder');
    } else {
      parts.add(formatFileSize(entry.sizeBytes));
    }
    // Show parent path
    final pathParts = entry.path.split('/');
    if (pathParts.length > 1) {
      parts.add(pathParts.sublist(0, pathParts.length - 1).join('/'));
    } else {
      parts.add('Vault root');
    }
    return parts.where((p) => p.isNotEmpty).join(' • ');
  }

  void _showSnackBar(BuildContext context, String message) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(message),
        duration: const Duration(seconds: 2),
      ),
    );
  }
}

class _ActionTile extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final Color? color;

  const _ActionTile({
    required this.icon,
    required this.label,
    required this.onTap,
    this.color,
  });

  @override
  Widget build(BuildContext context) {
    return ListTile(
      leading: Icon(icon, color: color),
      title: Text(label, style: color != null ? TextStyle(color: color) : null),
      dense: true,
      visualDensity: VisualDensity.compact,
      onTap: onTap,
    );
  }
}
