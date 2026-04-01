import 'dart:io';
import 'package:flutter/material.dart';
import 'package:file_picker/file_picker.dart';
import 'package:path_provider/path_provider.dart';
import 'package:path/path.dart' as p;
import 'package:jarvis_mobile/shared/utils/hash_utils.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/providers/clipboard_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/providers/file_operation_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/providers/selection_provider.dart';
import 'package:jarvis_mobile/features/explorer/domain/state/clipboard_state.dart';
import 'package:jarvis_mobile/features/explorer/presentation/widgets/breadcrumb_bar.dart';
import 'package:jarvis_mobile/features/explorer/presentation/widgets/file_context_menu.dart';
import 'package:jarvis_mobile/features/explorer/presentation/widgets/pdf_extract_dialog.dart';
import 'package:jarvis_mobile/features/explorer/presentation/widgets/file_search_delegate.dart';
import 'package:jarvis_mobile/features/explorer/presentation/widgets/selection_app_bar.dart';
import 'package:jarvis_mobile/features/editor/presentation/editor_screen.dart';
import 'package:jarvis_mobile/features/viewer/presentation/file_viewer_screen.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_list_screen.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_summary_dialog.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_screen.dart';
import 'package:jarvis_mobile/features/chat/presentation/chat_screen.dart';
import 'package:jarvis_mobile/core/network/server_connection_provider.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';

/// Main file explorer screen — displays files and directories in a list
/// with breadcrumb navigation, context menus, and selection support.
class ExplorerScreen extends ConsumerStatefulWidget {
  const ExplorerScreen({super.key});

  @override
  ConsumerState<ExplorerScreen> createState() => _ExplorerScreenState();
}

class _ExplorerScreenState extends ConsumerState<ExplorerScreen> {
  DateTime? _lastBackPress;
  String? _previousDir;

  @override
  Widget build(BuildContext context) {
    final currentDir = ref.watch(currentDirectoryProvider);
    final entriesAsync = ref.watch(directoryEntriesProvider);
    final syncState = ref.watch(syncProvider);
    final conflictCount = ref.watch(conflictCountProvider);
    final selectionState = ref.watch(selectionStateProvider);
    final clipboardState = ref.watch(clipboardStateProvider);
    final connectionState = ref.watch(serverConnectionProvider);
    final theme = Theme.of(context);

    // Clear selection when navigating to a different folder
    if (_previousDir != null && _previousDir != currentDir) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        ref.read(selectionStateProvider.notifier).clear();
      });
    }
    _previousDir = currentDir;

    final isSelectionMode = selectionState.isSelectionMode;

    return PopScope(
      canPop: false,
      onPopInvokedWithResult: (didPop, _) {
        if (didPop) return;

        // If in selection mode, exit selection first
        if (isSelectionMode) {
          ref.read(selectionStateProvider.notifier).clear();
          return;
        }

        // If in a subdirectory, navigate up
        if (currentDir.isNotEmpty) {
          final parts = currentDir.split('/');
          parts.removeLast();
          ref.read(currentDirectoryProvider.notifier).state = parts.join('/');
          return;
        }

        // At root: double-back-to-exit
        final now = DateTime.now();
        if (_lastBackPress != null &&
            now.difference(_lastBackPress!).inSeconds < 2) {
          Navigator.of(context).maybePop();
          return;
        }
        _lastBackPress = now;
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('Press back again to exit'),
            duration: Duration(seconds: 2),
          ),
        );
      },
      child: Scaffold(
        appBar: isSelectionMode
            ? const SelectionAppBar()
            : _buildAppBar(context, currentDir, conflictCount, clipboardState, syncState, connectionState),
        body: Column(
          children: [
            // Breadcrumb navigation bar
            const BreadcrumbBar(),
            // Sync status
            if (syncState.status == SyncStatus.syncing)
              const LinearProgressIndicator(),
            if (syncState.status == SyncStatus.error)
              MaterialBanner(
                content: Text(syncState.error ?? 'Sync failed'),
                backgroundColor: theme.colorScheme.errorContainer,
                actions: [
                  TextButton(
                    onPressed: () =>
                        ref.read(syncProvider.notifier).clearError(),
                    child: const Text('Dismiss'),
                  ),
                ],
              ),
            // Clipboard indicator
            if (clipboardState.isNotEmpty && !isSelectionMode)
              _buildClipboardBanner(context, clipboardState),
            // File list
            Expanded(
              child: entriesAsync.when(
                loading: () =>
                    const Center(child: CircularProgressIndicator()),
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
                            size: 64,
                            color: theme.colorScheme.onSurfaceVariant
                                .withValues(alpha: 0.3),
                          ),
                          const SizedBox(height: 12),
                          Text(
                            currentDir.isEmpty
                                ? 'Vault is empty'
                                : 'Empty folder',
                            style: theme.textTheme.titleMedium?.copyWith(
                              color: theme.colorScheme.onSurfaceVariant,
                            ),
                          ),
                          const SizedBox(height: 4),
                          Text(
                            currentDir.isEmpty
                                ? 'Tap sync to pull files from server'
                                : 'Create files or paste from clipboard',
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: theme.colorScheme.onSurfaceVariant
                                  .withValues(alpha: 0.7),
                            ),
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
                          _FileTile(
                            entry: entries[index],
                            isSelectionMode: isSelectionMode,
                            isSelected: selectionState.selectedIds
                                .contains(entries[index].path),
                          ),
                    ),
                  );
                },
              ),
            ),
          ],
        ),
        floatingActionButton: isSelectionMode
            ? null
            : Column(
                mainAxisAlignment: MainAxisAlignment.end,
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  FloatingActionButton(
                    heroTag: 'chat_fab',
                    onPressed: () {
                      Navigator.of(context).push(
                        MaterialPageRoute(
                            builder: (_) => const ChatScreen()),
                      );
                    },
                    child: const Icon(Icons.chat_bubble_outline),
                  ),
                  const SizedBox(height: 16),
                  FloatingActionButton.extended(
                    heroTag: 'sync_fab',
                    onPressed: syncState.status == SyncStatus.syncing
                        ? null
                        : () async {
                            await ref
                                .read(syncProvider.notifier)
                                .performSync();
                            ref.invalidate(directoryEntriesProvider);
                            if (!context.mounted) return;
                            final syncResult = ref.read(syncProvider).lastResult;
                            if (syncResult != null) {
                              showDialog(
                                context: context,
                                builder: (_) =>
                                    SyncSummaryDialog(result: syncResult),
                              );
                            }
                          },
                    icon: syncState.status == SyncStatus.syncing
                        ? const SizedBox(
                            width: 18,
                            height: 18,
                            child: CircularProgressIndicator(
                                strokeWidth: 2),
                          )
                        : const Icon(Icons.sync),
                    label: Text(syncState.status == SyncStatus.syncing
                        ? 'Syncing…'
                        : 'Sync'),
                  ),
                ],
              ),
      ),
    );
  }

  // ---------------------------------------------------------------------------
  // App Bar
  // ---------------------------------------------------------------------------

  PreferredSizeWidget _buildAppBar(
    BuildContext context,
    String currentDir,
    int conflictCount,
    ClipboardState clipboardState,
    dynamic syncState,
    ServerConnectionState connectionState,
  ) {
    final title = currentDir.isEmpty
        ? 'JARVIS Vault'
        : currentDir.split('/').last;

    return AppBar(
      title: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(title, style: const TextStyle(fontSize: 18)),
          Text(
            currentDir.isEmpty ? 'Root' : currentDir,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: Theme.of(context)
                      .colorScheme
                      .onSurface
                      .withValues(alpha: 0.6),
                  fontSize: 11,
                ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
        ],
      ),
      leading: currentDir.isEmpty
          ? null
          : IconButton(
              icon: const Icon(Icons.arrow_back),
              onPressed: () {
                final parts = currentDir.split('/');
                parts.removeLast();
                ref.read(currentDirectoryProvider.notifier).state =
                    parts.join('/');
              },
            ),
      actions: [
        // Search
        IconButton(
          icon: const Icon(Icons.search),
          tooltip: 'Search files',
          onPressed: () => _openSearch(context),
        ),
        // Connection status indicator
        IconButton(
          icon: _buildConnectionIcon(connectionState),
          tooltip: _getConnectionTooltip(connectionState),
          onPressed: () {
            ref.read(serverConnectionProvider.notifier).checkNow();
            ScaffoldMessenger.of(context).showSnackBar(
              const SnackBar(
                content: Text('Checking server connection...'),
                duration: Duration(seconds: 1),
              ),
            );
          },
        ),
        // Conflict badge
        if (conflictCount > 0)
          Badge.count(
            count: conflictCount,
            backgroundColor: Theme.of(context).colorScheme.error,
            child: IconButton(
              icon: const Icon(Icons.warning_amber_rounded),
              tooltip:
                  '$conflictCount conflict${conflictCount == 1 ? '' : 's'}',
              onPressed: () => Navigator.of(context).push(
                MaterialPageRoute(
                  builder: (_) => const ConflictListScreen(),
                ),
              ),
            ),
          ),
        // Create button
        PopupMenuButton<String>(
          icon: const Icon(Icons.add),
          tooltip: 'Create',
          onSelected: (value) {
            switch (value) {
              case 'file':
                _showCreateFileDialog(context, ref);
                break;
              case 'folder':
                _showCreateFolderDialog(context, ref);
                break;
              case 'import':
                _importFile(context, ref);
                break;
            }
          },
          itemBuilder: (_) => [
            const PopupMenuItem(
              value: 'file',
              child: ListTile(
                leading: Icon(Icons.note_add_outlined),
                title: Text('New File'),
                dense: true,
                contentPadding: EdgeInsets.zero,
              ),
            ),
            const PopupMenuItem(
              value: 'folder',
              child: ListTile(
                leading: Icon(Icons.create_new_folder_outlined),
                title: Text('New Folder'),
                dense: true,
                contentPadding: EdgeInsets.zero,
              ),
            ),
            const PopupMenuItem(
              value: 'import',
              child: ListTile(
                leading: Icon(Icons.file_upload_outlined),
                title: Text('Import File'),
                dense: true,
                contentPadding: EdgeInsets.zero,
              ),
            ),
          ],
        ),
        // Overflow menu
        PopupMenuButton<String>(
          onSelected: (value) {
            switch (value) {
              case 'settings':
                Navigator.of(context).push(
                  MaterialPageRoute(
                      builder: (_) => const SettingsScreen()),
                );
              case 'reset':
                _showResetDialog(context, ref);
            }
          },
          itemBuilder: (_) => [
            const PopupMenuItem(
              value: 'settings',
              child: ListTile(
                leading: Icon(Icons.settings_outlined),
                title: Text('Settings'),
                dense: true,
                contentPadding: EdgeInsets.zero,
              ),
            ),
            const PopupMenuItem(
              value: 'reset',
              child: ListTile(
                leading: Icon(Icons.delete_forever, color: Colors.red),
                title: Text('Reset All',
                    style: TextStyle(color: Colors.red)),
                dense: true,
                contentPadding: EdgeInsets.zero,
              ),
            ),
          ],
        ),
      ],
    );
  }

  // ---------------------------------------------------------------------------
  // Clipboard Banner
  // ---------------------------------------------------------------------------

  Widget _buildClipboardBanner(
      BuildContext context, ClipboardState clipboardState) {
    final theme = Theme.of(context);
    final isCut = clipboardState.operation == ClipboardOperation.cut;
    final count = clipboardState.fileIds.length;

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
      color: isCut
          ? theme.colorScheme.tertiaryContainer.withValues(alpha: 0.5)
          : theme.colorScheme.secondaryContainer.withValues(alpha: 0.5),
      child: Row(
        children: [
          Icon(
            isCut ? Icons.content_cut : Icons.content_copy,
            size: 16,
            color: theme.colorScheme.onSecondaryContainer,
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              '$count item${count == 1 ? '' : 's'} ${isCut ? 'cut' : 'copied'}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onSecondaryContainer,
                fontWeight: FontWeight.w500,
              ),
            ),
          ),
          TextButton.icon(
            onPressed: () => _executePaste(context, ref),
            icon: const Icon(Icons.paste, size: 16),
            label: const Text('Paste here'),
            style: TextButton.styleFrom(
              visualDensity: VisualDensity.compact,
              padding: const EdgeInsets.symmetric(horizontal: 12),
            ),
          ),
          IconButton(
            icon: const Icon(Icons.close, size: 16),
            onPressed: () =>
                ref.read(clipboardStateProvider.notifier).clear(),
            visualDensity: VisualDensity.compact,
            tooltip: 'Clear clipboard',
          ),
        ],
      ),
    );
  }

  // ---------------------------------------------------------------------------
  // Search
  // ---------------------------------------------------------------------------

  void _openSearch(BuildContext context) {
    final repo = ref.read(explorerRepositoryProvider);

    showSearch(
      context: context,
      delegate: FileSearchDelegate(
        repository: repo,
        onNavigate: (dir) {
          ref.read(currentDirectoryProvider.notifier).state = dir;
        },
        onOpen: (entry) {
          if (entry.localPath != null) {
            Navigator.of(context).push(
              MaterialPageRoute(
                builder: (_) => EditorScreen(filePath: entry.path),
              ),
            );
          }
        },
      ),
    );
  }

  // ---------------------------------------------------------------------------
  // Context Menu
  // ---------------------------------------------------------------------------

  void _showContextMenu(BuildContext context, FileEntry entry) async {
    final result = await showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => FileContextMenu(entry: entry),
    );

    if (!context.mounted || result == null) return;

    switch (result) {
      case 'open':
        if (entry.localPath != null) {
          Navigator.of(context).push(
            MaterialPageRoute(
              builder: (_) => EditorScreen(filePath: entry.path),
            ),
          );
        }
      case 'rename':
        _showRenameDialog(context, ref, entry);
      case 'extract-pdf':
        _showPdfExtractDialog(context, ref, entry);
      case 'select':
        ref.read(selectionStateProvider.notifier).toggle(entry.path);
      case 'delete':
        _confirmAndDelete(context, ref, entry);
    }
  }

  // ---------------------------------------------------------------------------
  // Extract PDF
  // ---------------------------------------------------------------------------

  Future<void> _showPdfExtractDialog(
      BuildContext context, WidgetRef ref, FileEntry entry) async {
    final result = await showDialog<Map<String, dynamic>>(
      context: context,
      builder: (_) => PdfExtractDialog(fileName: entry.name),
    );

    if (result == null || !context.mounted) return;

    final start = result['start'] as int;
    final end = result['end'] as int?;

    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text('Extracting pages from ${entry.name}...')),
    );

    try {
      final apiClient = ref.read(apiClientProvider);
      
      final payload = {
        'path': entry.path,
        'start_page': start,
        if (end != null) 'end_page': end,
      };

      final response = await apiClient.dio.post(
        '/ask/extract-pdf',
        data: payload,
      );

      final markdown = response.data['markdown'] as String;
      
      if (!context.mounted) return;

      final currentDir = ref.read(currentDirectoryProvider);
      final newFileName = '${entry.name}_pages_$start${end != null ? '-$end' : ''}_extracted.md';
      final newPath = currentDir.isEmpty ? newFileName : '$currentDir/$newFileName';

      // Get local directory
      final docsDir = await getApplicationDocumentsDirectory();
      final localFile = File(p.join(docsDir.path, 'jarvis_mirror', newPath.replaceAll('/', Platform.pathSeparator)));
      
      if (!localFile.parent.existsSync()) {
        localFile.parent.createSync(recursive: true);
      }
      localFile.writeAsStringSync(markdown);
      
      final mtime = DateTime.now().toUtc().toIso8601String();
      final hash = sha256String(markdown);

      final repo = ref.read(explorerRepositoryProvider);
      await repo.upsertFile(FileEntry(
        path: newPath,
        name: newFileName,
        type: 'file',
        sizeBytes: markdown.length,
        lastModified: mtime,
        contentHash: hash,
        localPath: localFile.path,
      ));

      final db = ref.read(appDatabaseProvider);
      await db.enqueueMutation(
        id: 'create-${DateTime.now().millisecondsSinceEpoch}-${newPath.hashCode}',
        operation: 'create',
        path: newPath,
        timestamp: DateTime.now().toUtc().toIso8601String(),
        baseVersion: 1,
      );

      ref.invalidate(directoryEntriesProvider);
      
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Saved extracted text to $newFileName')),
        );
      }

    } catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text('Extraction failed: $e'),
            backgroundColor: Theme.of(context).colorScheme.error,
          ),
        );
      }
    }
  }

  // ---------------------------------------------------------------------------
  // Rename
  // ---------------------------------------------------------------------------

  Future<void> _showRenameDialog(
      BuildContext context, WidgetRef ref, FileEntry entry) async {
    
    int? descendantCount;
    if (entry.isDirectory) {
      final repo = ref.read(explorerRepositoryProvider);
      descendantCount = await repo.getDescendantCount(entry.path);
    }
    
    if (!context.mounted) return;
    
    final controller = TextEditingController(text: entry.name);
    final newName = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Rename'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            TextField(
              controller: controller,
              autofocus: true,
              decoration: const InputDecoration(
                labelText: 'New name',
                border: OutlineInputBorder(),
              ),
              onSubmitted: (v) => Navigator.pop(ctx, v.trim()),
            ),
            if (descendantCount != null && descendantCount > 0)
              Padding(
                padding: const EdgeInsets.only(top: 16.0),
                child: Text(
                  'This will sync rename for all $descendantCount items inside this folder.',
                  style: Theme.of(ctx).textTheme.bodySmall?.copyWith(
                    color: Theme.of(ctx).colorScheme.error,
                  ),
                ),
              ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(ctx, controller.text.trim()),
            child: const Text('Rename'),
          ),
        ],
      ),
    );

    if (newName == null || newName.isEmpty || newName == entry.name) return;
    if (!context.mounted) return;

    final service = ref.read(fileOperationServiceProvider);
    final result = await service.renameFile(entry.path, newName);
    ref.invalidate(directoryEntriesProvider);

    if (!context.mounted) return;
    if (result.isSuccess) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Renamed to "$newName"')),
      );
    } else {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Rename failed: ${result.errors.first.message}'),
          backgroundColor: Theme.of(context).colorScheme.error,
        ),
      );
    }
  }

  // ---------------------------------------------------------------------------
  // Delete single file (from context menu)
  // ---------------------------------------------------------------------------

  Future<void> _confirmAndDelete(
      BuildContext context, WidgetRef ref, FileEntry entry) async {
    
    int? descendantCount;
    if (entry.isDirectory) {
      final repo = ref.read(explorerRepositoryProvider);
      descendantCount = await repo.getDescendantCount(entry.path);
    }
    
    final message = entry.isDirectory 
        ? 'Delete folder "${entry.name}" and all its ${descendantCount!} items?\n\nThis will be synced to the server.'
        : 'Delete "${entry.name}"?\n\nThis will be synced to the server.';

    if (!context.mounted) return;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Delete'),
        content: Text(message),
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
            child: const Text('Delete'),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) return;

    final service = ref.read(fileOperationServiceProvider);
    final result = await service.deleteFile(entry.path);
    ref.invalidate(directoryEntriesProvider);

    if (!context.mounted) return;
    if (result.isSuccess) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Deleted "${entry.name}"')),
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

  // ---------------------------------------------------------------------------
  // Paste
  // ---------------------------------------------------------------------------

  Future<void> _executePaste(BuildContext context, WidgetRef ref) async {
    final currentDir = ref.read(currentDirectoryProvider);
    final clipboardNotifier = ref.read(clipboardStateProvider.notifier);
    final service = ref.read(fileOperationServiceProvider);
    final clipboardState = ref.read(clipboardStateProvider);

    final itemCount = clipboardState.fileIds.length;
    final opName =
        clipboardState.operation == ClipboardOperation.cut ? 'Moving' : 'Copying';

    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('$opName $itemCount item${itemCount == 1 ? '' : 's'}…'),
          duration: const Duration(seconds: 1),
        ),
      );
    }

    final result = await clipboardNotifier.paste(currentDir, service);

    ref.invalidate(directoryEntriesProvider);

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

  // ---------------------------------------------------------------------------
  // Create File / Folder / Import
  // ---------------------------------------------------------------------------

  Future<void> _showCreateFileDialog(BuildContext context, WidgetRef ref) async {
    final controller = TextEditingController();
    final fileName = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('New File'),
        content: TextField(
          controller: controller,
          autofocus: true,
          decoration: const InputDecoration(
            labelText: 'File name',
            hintText: 'e.g. notes.md',
            border: OutlineInputBorder(),
          ),
          onSubmitted: (v) => Navigator.of(ctx).pop(v.trim()),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(controller.text.trim()),
            child: const Text('Create'),
          ),
        ],
      ),
    );

    if (fileName == null || fileName.isEmpty || !context.mounted) return;

    try {
      final currentDir = ref.read(currentDirectoryProvider);
      final path =
          currentDir.isEmpty ? fileName : '$currentDir/$fileName';
      
      // Get local directory
      final docsDir = await getApplicationDocumentsDirectory();
      final localFile = File(p.join(docsDir.path, 'jarvis_mirror', path.replaceAll('/', Platform.pathSeparator)));
      
      // Physically create file
      if (!localFile.parent.existsSync()) {
        localFile.parent.createSync(recursive: true);
      }
      localFile.writeAsStringSync('');
      
      final mtime = DateTime.now().toUtc().toIso8601String();
      final hash = sha256String('');

      final repo = ref.read(explorerRepositoryProvider);
      await repo.upsertFile(FileEntry(
        path: path,
        name: fileName,
        type: 'file',
        sizeBytes: 0,
        lastModified: mtime,
        contentHash: hash,
        localPath: localFile.path,
      ));

      // Enqueue creation mutation
      final db = ref.read(appDatabaseProvider);
      await db.enqueueMutation(
        id: 'create-${DateTime.now().millisecondsSinceEpoch}-${path.hashCode}',
        path: path,
        operation: 'create',
        timestamp: mtime,
        baseVersion: 1,
      );

      ref.invalidate(directoryEntriesProvider);

      if (context.mounted) {
        Navigator.of(context).push(
          MaterialPageRoute(
            builder: (_) => EditorScreen(filePath: path),
          ),
        );
      }
    } catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text('Failed to create file: $e'),
            backgroundColor: Theme.of(context).colorScheme.error,
          ),
        );
      }
    }
  }

  Future<void> _importFile(BuildContext context, WidgetRef ref) async {
    final result = await FilePicker.platform.pickFiles(allowMultiple: true);
    if (result == null || result.files.isEmpty || !context.mounted) return;

    final currentDir = ref.read(currentDirectoryProvider);
    final repo = ref.read(explorerRepositoryProvider);
    final db = ref.read(appDatabaseProvider);
    final service = ref.read(fileOperationServiceProvider);
    
    final docsDir = await getApplicationDocumentsDirectory();
    final mirrorDir = Directory(p.join(docsDir.path, 'jarvis_mirror'));

    int successCount = 0;

    for (final file in result.files) {
      if (file.path == null || file.name.isEmpty) continue;

      try {
        final sourceFile = File(file.path!);
        
        // Find a unique name
        final uniqueName = await service.generateUniqueName(file.name, currentDir);
        final uniquePath = currentDir.isEmpty ? uniqueName : '$currentDir/$uniqueName';
        
        final localFile = File(p.join(mirrorDir.path, uniquePath.replaceAll('/', Platform.pathSeparator)));
        if (!localFile.parent.existsSync()) {
          localFile.parent.createSync(recursive: true);
        }
        
        await sourceFile.copy(localFile.path);
        
        final bytes = await localFile.readAsBytes();
        final hash = sha256Hex(bytes);
        final mtime = DateTime.now().toUtc().toIso8601String();

        await repo.upsertFile(FileEntry(
          path: uniquePath,
          name: uniqueName,
          type: 'file',
          sizeBytes: bytes.length,
          lastModified: mtime,
          contentHash: hash,
          localPath: localFile.path,
        ));

        await db.enqueueMutation(
          id: 'create-${DateTime.now().millisecondsSinceEpoch}-${uniquePath.hashCode}',
          path: uniquePath,
          operation: 'create',
          timestamp: mtime,
          baseVersion: 1,
        );
        successCount++;
      } catch (e) {
        if (context.mounted) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(
              content: Text('Failed to import ${file.name}: $e'),
              backgroundColor: Theme.of(context).colorScheme.error,
            ),
          );
        }
      }
    }

    ref.invalidate(directoryEntriesProvider);
    
    if (context.mounted && successCount > 0) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Imported $successCount file(s)')),
      );
    }
  }

  Future<void> _showCreateFolderDialog(
      BuildContext context, WidgetRef ref) async {
    final controller = TextEditingController();
    final folderName = await showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('New Folder'),
        content: TextField(
          controller: controller,
          autofocus: true,
          decoration: const InputDecoration(
            labelText: 'Folder name',
            hintText: 'e.g. Documents',
            border: OutlineInputBorder(),
          ),
          onSubmitted: (v) => Navigator.of(ctx).pop(v.trim()),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(controller.text.trim()),
            child: const Text('Create'),
          ),
        ],
      ),
    );

    if (folderName == null || folderName.isEmpty || !context.mounted) return;

    try {
      final currentDir = ref.read(currentDirectoryProvider);
      final path =
          currentDir.isEmpty ? folderName : '$currentDir/$folderName';
      final repo = ref.read(explorerRepositoryProvider);
      // Create a placeholder file inside the folder so it appears in the tree
      await repo.upsertFile(FileEntry(
        path: '$path/.folder',
        name: '.folder',
        type: 'file',
        sizeBytes: 0,
        lastModified: DateTime.now().toUtc().toIso8601String(),
      ));
      ref.invalidate(directoryEntriesProvider);

      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Created folder "$folderName"')),
        );
      }
    } catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text('Failed to create folder: $e'),
            backgroundColor: Theme.of(context).colorScheme.error,
          ),
        );
      }
    }
  }

  // ---------------------------------------------------------------------------
  // Reset
  // ---------------------------------------------------------------------------

  Future<void> _showResetDialog(BuildContext context, WidgetRef ref) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Reset Everything?'),
        content: const Text(
          'This will DELETE ALL FILES from both the server and '
          'your device — including the mutation queue.\n\n'
          'This action cannot be undone.',
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
            child: const Text('Reset Everything'),
          ),
        ],
      ),
    );

    if (confirmed != true || !context.mounted) return;

    try {
      final syncRepo = ref.read(syncRepositoryProvider);
      await syncRepo.resetEverything();
      ref.invalidate(directoryEntriesProvider);

      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('Everything has been reset.'),
            duration: Duration(seconds: 2),
          ),
        );
      }
    } catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text('Reset failed: $e'),
            backgroundColor: Theme.of(context).colorScheme.error,
          ),
        );
      }
    }
  }

  Widget _buildConnectionIcon(ServerConnectionState state) {
    switch (state) {
      case ServerConnectionState.checking:
        return const SizedBox(
          width: 20,
          height: 20,
          child: CircularProgressIndicator(strokeWidth: 2),
        );
      case ServerConnectionState.online:
        return Icon(
          Icons.cloud_done_outlined,
          color: Colors.green.shade400,
        );
      case ServerConnectionState.offline:
        return Icon(
          Icons.cloud_off_outlined,
          color: Colors.orange.shade400,
        );
    }
  }

  String _getConnectionTooltip(ServerConnectionState state) {
    switch (state) {
      case ServerConnectionState.checking:
        return 'Checking connection...';
      case ServerConnectionState.online:
        return 'Connected to Server';
      case ServerConnectionState.offline:
        return 'Offline - Tap to reconnect';
    }
  }
}

// =============================================================================
// File Tile Widget
// =============================================================================

/// Improved file tile with selection support, context menu, and better design.
class _FileTile extends ConsumerWidget {
  final FileEntry entry;
  final bool isSelectionMode;
  final bool isSelected;

  const _FileTile({
    required this.entry,
    required this.isSelectionMode,
    required this.isSelected,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final isConflict = entry.name.contains('_conflict_');

    return AnimatedContainer(
      duration: const Duration(milliseconds: 200),
      color: isSelected
          ? theme.colorScheme.primaryContainer.withValues(alpha: 0.4)
          : Colors.transparent,
      child: ListTile(
        leading: _buildLeading(theme),
        title: Text(
          entry.name,
          style: TextStyle(
            color: isConflict ? theme.colorScheme.error : null,
            fontWeight: FontWeight.w500,
          ),
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
        ),
        subtitle: _buildSubtitle(theme),
        trailing: _buildTrailing(theme),
        onTap: () => _handleTap(context, ref),
        onLongPress: () => _handleLongPress(context, ref),
      ),
    );
  }

  Widget _buildLeading(ThemeData theme) {
    if (isSelectionMode) {
      return Stack(
        alignment: Alignment.center,
        children: [
          Icon(
            entry.isDirectory ? Icons.folder : Icons.description_outlined,
            color: isSelected
                ? theme.colorScheme.primary
                : theme.colorScheme.onSurfaceVariant,
            size: 28,
          ),
          Positioned(
            right: -2,
            bottom: -2,
            child: AnimatedScale(
              scale: isSelected ? 1.0 : 0.0,
              duration: const Duration(milliseconds: 150),
              child: Container(
                width: 16,
                height: 16,
                decoration: BoxDecoration(
                  color: theme.colorScheme.primary,
                  shape: BoxShape.circle,
                  border: Border.all(
                    color: theme.colorScheme.surface,
                    width: 1.5,
                  ),
                ),
                child: Icon(
                  Icons.check,
                  size: 10,
                  color: theme.colorScheme.onPrimary,
                ),
              ),
            ),
          ),
        ],
      );
    }

    return Icon(
      entry.isDirectory
          ? Icons.folder
          : entry.name.contains('_conflict_')
              ? Icons.warning_amber
              : _getFileIcon(entry.name),
      color: entry.isDirectory
          ? theme.colorScheme.primary
          : entry.name.contains('_conflict_')
              ? theme.colorScheme.error
              : theme.colorScheme.onSurfaceVariant,
      size: 28,
    );
  }

  Widget? _buildSubtitle(ThemeData theme) {
    if (entry.isDirectory) return null;

    final parts = <String>[];
    if (entry.sizeBytes != null && entry.sizeBytes! > 0) {
      parts.add(formatFileSize(entry.sizeBytes));
    }
    parts.add(entry.isSynced ? 'Synced' : 'Server only');

    return Text(
      parts.join(' • '),
      style: theme.textTheme.bodySmall?.copyWith(
        color: theme.colorScheme.onSurfaceVariant,
      ),
    );
  }

  Widget? _buildTrailing(ThemeData theme) {
    if (isSelectionMode) {
      return Checkbox(
        value: isSelected,
        onChanged: null, // Visual only, tap handles toggle
        shape: const CircleBorder(),
      );
    }
    if (entry.isDirectory) {
      return const Icon(Icons.chevron_right, size: 20);
    }
    return null;
  }

  void _handleTap(BuildContext context, WidgetRef ref) {
    if (isSelectionMode) {
      ref.read(selectionStateProvider.notifier).toggle(entry.path);
    } else if (entry.isDirectory) {
      ref.read(currentDirectoryProvider.notifier).state = entry.path;
    } else if (entry.localPath != null) {
      final ext = entry.name.contains('.') ? entry.name.split('.').last.toLowerCase() : '';
      final textExtensions = ['md', 'txt', 'log', 'json', 'yaml', 'yml', 'xml', 'toml', 'dart', 'py', 'js', 'ts', 'java', 'kt', 'swift', 'rs', 'go', 'c', 'cpp', 'h'];
      
      if (textExtensions.contains(ext) || ext.isEmpty) {
        Navigator.of(context).push(
          MaterialPageRoute(
            builder: (_) => EditorScreen(filePath: entry.path),
          ),
        );
      } else {
        Navigator.of(context).push(
          MaterialPageRoute(
            builder: (_) => FileViewerScreen(
              localPath: entry.localPath!,
              fileName: entry.name,
            ),
          ),
        );
      }
    }
  }

  void _handleLongPress(BuildContext context, WidgetRef ref) {
    if (isSelectionMode) {
      // In selection mode, toggle
      ref.read(selectionStateProvider.notifier).toggle(entry.path);
    } else {
      // Show context menu
      final explorerState = context.findAncestorStateOfType<_ExplorerScreenState>();
      explorerState?._showContextMenu(context, entry);
    }
  }

  IconData _getFileIcon(String name) {
    final ext = name.contains('.') ? name.split('.').last.toLowerCase() : '';
    switch (ext) {
      case 'md':
      case 'txt':
      case 'log':
        return Icons.article_outlined;
      case 'json':
      case 'yaml':
      case 'yml':
      case 'xml':
      case 'toml':
        return Icons.data_object;
      case 'dart':
      case 'py':
      case 'js':
      case 'ts':
      case 'java':
      case 'kt':
      case 'swift':
      case 'rs':
      case 'go':
      case 'c':
      case 'cpp':
      case 'h':
        return Icons.code;
      case 'jpg':
      case 'jpeg':
      case 'png':
      case 'gif':
      case 'webp':
      case 'svg':
      case 'bmp':
        return Icons.image_outlined;
      case 'pdf':
        return Icons.picture_as_pdf_outlined;
      case 'zip':
      case 'tar':
      case 'gz':
      case 'rar':
      case '7z':
        return Icons.folder_zip_outlined;
      default:
        return Icons.description_outlined;
    }
  }
}
