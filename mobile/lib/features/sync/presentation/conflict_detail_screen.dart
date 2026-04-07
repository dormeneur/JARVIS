import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/sync_provider.dart';
import 'package:jarvis_mobile/shared/utils/file_type_utils.dart';

/// Conflict detail screen with a 2-step resolution flow:
/// 1. Compare: read-only Local and Remote tabs
/// 2. Edit: pick a base, optionally edit, then save & queue for sync
///
/// For binary files (PDF, DOCX, images) the editing step is skipped
/// — the user picks "Keep Local" or "Use Remote" directly.
class ConflictDetailScreen extends ConsumerStatefulWidget {
  final MutationQueueData mutation;

  const ConflictDetailScreen({super.key, required this.mutation});

  @override
  ConsumerState<ConflictDetailScreen> createState() =>
      _ConflictDetailScreenState();
}

class _ConflictDetailScreenState extends ConsumerState<ConflictDetailScreen>
    with SingleTickerProviderStateMixin {
  late TabController _tabController;

  // Content holders
  String _localContent = '';
  String _remoteContent = '';
  bool _isLoading = true;
  String? _error;

  // Editor state (Step 2)
  bool _isEditing = false;
  final _editController = TextEditingController();

  /// Whether this conflict is for a binary file that cannot be text-diffed.
  late final bool _isBinaryFile;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 2, vsync: this);
    final mode = fileOpenMode(widget.mutation.path.split('/').last);
    _isBinaryFile = mode == FileOpenMode.viewer;
    _loadContents();
  }

  @override
  void dispose() {
    _tabController.dispose();
    _editController.dispose();
    super.dispose();
  }

  Future<void> _loadContents() async {
    setState(() {
      _isLoading = true;
      _error = null;
    });

    try {
      // Local content: read from localContentSnapshot in the mutation row
      _localContent =
          widget.mutation.localContentSnapshot ??
          '(no local snapshot available)';

      // Remote content: fetch from server
      final syncRepo = ref.read(syncRepositoryProvider);
      final remote = await syncRepo.fetchRemoteContent(widget.mutation.path);
      _remoteContent = remote ?? '(failed to fetch remote content)';

      setState(() => _isLoading = false);
    } catch (e) {
      setState(() {
        _isLoading = false;
        _error = e.toString();
      });
    }
  }

  void _pickBase(String content) {
    if (_isBinaryFile) {
      // For binary files, skip the editing step entirely
      _editController.text = content;
      _saveAndQueue();
      return;
    }
    setState(() {
      _isEditing = true;
      _editController.text = content;
    });
  }

  Future<void> _saveAndQueue() async {
    final notifier = ref.read(conflictNotifierProvider.notifier);
    await notifier.resolveConflict(widget.mutation.id, _editController.text);

    if (!mounted) return;

    final state = ref.read(conflictNotifierProvider);
    if (!context.mounted) return;
    if (state.hasError) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Resolution failed: ${state.error}'),
          backgroundColor: Theme.of(context).colorScheme.error,
        ),
      );
    } else {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('Conflict resolved. Will sync on next push.'),
          duration: Duration(seconds: 2),
        ),
      );
      Navigator.of(context).pop();
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fileName = widget.mutation.path.split('/').last;
    final resolving = ref.watch(conflictNotifierProvider);

    if (_isEditing) {
      return _buildEditorView(theme, fileName, resolving);
    }
    return _buildCompareView(theme, fileName, resolving);
  }

  /// Step 1: Compare local vs remote (read-only tabs)
  Widget _buildCompareView(ThemeData theme, String fileName, AsyncValue resolving) {
    return Scaffold(
      appBar: AppBar(
        title: Text(fileName),
        bottom: _isBinaryFile
            ? null
            : TabBar(
                controller: _tabController,
                tabs: const [
                  Tab(icon: Icon(Icons.phone_android), text: 'Local'),
                  Tab(icon: Icon(Icons.cloud_outlined), text: 'Remote'),
                ],
              ),
      ),
      body: _isLoading
          ? const Center(child: CircularProgressIndicator())
          : _error != null
          ? Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      Icons.error_outline,
                      size: 48,
                      color: theme.colorScheme.error,
                    ),
                    const SizedBox(height: 12),
                    Text('Error: $_error', textAlign: TextAlign.center),
                    const SizedBox(height: 12),
                    FilledButton(
                      onPressed: _loadContents,
                      child: const Text('Retry'),
                    ),
                  ],
                ),
              ),
            )
          : _isBinaryFile
              ? _buildBinaryCompareBody(theme, fileName)
              : TabBarView(
                  controller: _tabController,
                  children: [
                    _contentView(_localContent, theme),
                    _contentView(_remoteContent, theme),
                  ],
                ),
      bottomNavigationBar: _isLoading || _error != null
          ? null
          : SafeArea(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Row(
                      children: [
                        Expanded(
                          child: OutlinedButton.icon(
                            onPressed: resolving.isLoading
                                ? null
                                : () => _pickBase(_localContent),
                            icon: const Icon(Icons.phone_android),
                            label: Text(_isBinaryFile ? 'Keep Local' : 'Use Local'),
                          ),
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: FilledButton.icon(
                            onPressed: resolving.isLoading
                                ? null
                                : () => _pickBase(_remoteContent),
                            icon: const Icon(Icons.cloud_outlined),
                            label: Text(_isBinaryFile ? 'Use Remote' : 'Use Remote'),
                          ),
                        ),
                      ],
                    ),
                    const SizedBox(height: 8),
                    SizedBox(
                      width: double.infinity,
                      child: TextButton.icon(
                        onPressed: resolving.isLoading
                            ? null
                            : () => _confirmAndDelete(context),
                        icon: Icon(Icons.delete_forever, size: 18, color: theme.colorScheme.error),
                        label: Text(
                          'Delete from all devices',
                          style: TextStyle(color: theme.colorScheme.error),
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
    );
  }

  /// Body for binary file conflicts — a simple informational card.
  Widget _buildBinaryCompareBody(ThemeData theme, String fileName) {
    final ext = fileExtension(fileName).toUpperCase();
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              _binaryIcon(fileName),
              size: 72,
              color: theme.colorScheme.primary.withValues(alpha: 0.6),
            ),
            const SizedBox(height: 20),
            Text(
              '$ext File Conflict',
              style: theme.textTheme.titleLarge?.copyWith(
                fontWeight: FontWeight.w600,
              ),
            ),
            const SizedBox(height: 12),
            Text(
              'This is a binary file that cannot be compared as text.\n'
              'Choose which version to keep.',
              textAlign: TextAlign.center,
              style: theme.textTheme.bodyMedium?.copyWith(
                color: theme.colorScheme.onSurfaceVariant,
              ),
            ),
            const SizedBox(height: 24),
            Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  children: [
                    Row(
                      children: [
                        const Icon(Icons.phone_android, size: 18),
                        const SizedBox(width: 8),
                        const Expanded(
                          child: Text('Local', style: TextStyle(fontWeight: FontWeight.w600)),
                        ),
                        Text(
                          widget.mutation.localContentSnapshot != null
                              ? '${(widget.mutation.localContentSnapshot!.length / 1024).toStringAsFixed(1)} KB'
                              : 'Unknown size',
                          style: theme.textTheme.bodySmall,
                        ),
                      ],
                    ),
                    const Divider(height: 24),
                    Row(
                      children: [
                        const Icon(Icons.cloud_outlined, size: 18),
                        const SizedBox(width: 8),
                        const Expanded(
                          child: Text('Remote', style: TextStyle(fontWeight: FontWeight.w600)),
                        ),
                        Text(
                          _remoteContent.isNotEmpty
                              ? '${(_remoteContent.length / 1024).toStringAsFixed(1)} KB'
                              : 'Unknown size',
                          style: theme.textTheme.bodySmall,
                        ),
                      ],
                    ),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  IconData _binaryIcon(String fileName) {
    final ext = fileExtension(fileName);
    if (ext == 'pdf') return Icons.picture_as_pdf_outlined;
    if (ext == 'docx' || ext == 'doc') return Icons.description_outlined;
    if ({'jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp'}.contains(ext)) {
      return Icons.image_outlined;
    }
    return Icons.insert_drive_file_outlined;
  }

  Future<void> _confirmAndDelete(BuildContext context) async {
    final fileName = widget.mutation.path.split('/').last;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Delete from all devices?'),
        content: Text(
          'This will permanently delete "$fileName" from the server '
          'and this device. This cannot be undone.',
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
    await notifier.deleteConflict(widget.mutation.id);

    if (!mounted) return;

    final state = ref.read(conflictNotifierProvider);
    if (!context.mounted) return;
    
    if (state.hasError) {
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text('Delete failed: ${state.error}'),
          backgroundColor: Theme.of(context).colorScheme.error,
        ),
      );
    } else {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text('File deleted from all devices.'),
          duration: Duration(seconds: 2),
        ),
      );
      Navigator.of(context).pop();
    }
  }

  /// Step 2: Editable text with save button
  Widget _buildEditorView(
    ThemeData theme,
    String fileName,
    AsyncValue resolving,
  ) {
    return Scaffold(
      appBar: AppBar(
        title: Text('Edit: $fileName'),
        leading: IconButton(
          icon: const Icon(Icons.arrow_back),
          onPressed: () => setState(() => _isEditing = false),
        ),
      ),
      body: Padding(
        padding: const EdgeInsets.all(12),
        child: TextField(
          controller: _editController,
          maxLines: null,
          expands: true,
          style: const TextStyle(fontFamily: 'monospace', fontSize: 13),
          decoration: const InputDecoration(
            border: OutlineInputBorder(),
            hintText: 'Edit the final content...',
          ),
          textAlignVertical: TextAlignVertical.top,
        ),
      ),
      bottomNavigationBar: SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: FilledButton.icon(
            onPressed: resolving.isLoading ? null : _saveAndQueue,
            icon: resolving.isLoading
                ? const SizedBox(
                    width: 18,
                    height: 18,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: Colors.white,
                    ),
                  )
                : const Icon(Icons.save),
            label: const Text('Save & Queue for Sync'),
          ),
        ),
      ),
    );
  }

  Widget _contentView(String content, ThemeData theme) {
    return SingleChildScrollView(
      padding: const EdgeInsets.all(12),
      child: SelectableText(
        content,
        style: TextStyle(
          fontFamily: 'monospace',
          fontSize: 13,
          color: theme.colorScheme.onSurface,
        ),
      ),
    );
  }
}
