import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/features/editor/presentation/editor_screen.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/sync/presentation/conflict_provider.dart';

/// Shows both sides of a conflict and lets the user choose a resolution.
///
/// Tabs:
///   Local  – the user's edit (from local mirror)
///   Remote – the server's current content (conflict file, if pulled)
///
/// Actions:
///   Keep Local    → re-queues mutation with fresh base_version
///   Accept Remote → overwrites local with remote content, removes mutation
///   Edit Merged   → opens EditorScreen for manual editing; user must
///                   then tap "Resolved" to confirm and re-queue
class ConflictDetailScreen extends ConsumerStatefulWidget {
  final MutationQueueData mutation;

  const ConflictDetailScreen({super.key, required this.mutation});

  @override
  ConsumerState<ConflictDetailScreen> createState() =>
      _ConflictDetailScreenState();
}

class _ConflictDetailScreenState extends ConsumerState<ConflictDetailScreen>
    with SingleTickerProviderStateMixin {
  late final TabController _tabController;
  String? _localContent;
  String? _remoteContent;
  bool _loadingLocal = true;
  bool _loadingRemote = true;

  @override
  void initState() {
    super.initState();
    _tabController = TabController(length: 2, vsync: this);
    _loadContents();
  }

  @override
  void dispose() {
    _tabController.dispose();
    super.dispose();
  }

  Future<void> _loadContents() async {
    final repo = ref.read(explorerRepositoryProvider);
    final entry = await repo.getEntry(widget.mutation.path);

    // Load local content
    if (entry?.localPath != null) {
      final file = File(entry!.localPath!);
      if (file.existsSync()) {
        final content = await file.readAsString();
        if (mounted) setState(() => _localContent = content);
      }
    }
    if (mounted) setState(() => _loadingLocal = false);

    // Load remote conflict file content (if available)
    final conflictPath = widget.mutation.conflictFilePath;
    if (conflictPath != null && conflictPath.isNotEmpty) {
      final conflictEntry = await repo.getEntry(conflictPath);
      if (conflictEntry?.localPath != null) {
        final file = File(conflictEntry!.localPath!);
        if (file.existsSync()) {
          final content = await file.readAsString();
          if (mounted) setState(() => _remoteContent = content);
        }
      }
    }
    if (mounted) setState(() => _loadingRemote = false);
  }

  Future<void> _keepLocal(BuildContext context) async {
    final notifier = ref.read(conflictNotifierProvider.notifier);
    await notifier.resolveKeepLocal(widget.mutation.id);

    if (!mounted) return;
    final state = ref.read(conflictNotifierProvider);
    if (state.error != null) {
      _showError(context, state.error!);
    } else {
      _showSuccess(context, 'Local version kept. Sync to push.');
      Navigator.of(context).pop();
    }
  }

  Future<void> _acceptRemote(BuildContext context) async {
    final notifier = ref.read(conflictNotifierProvider.notifier);
    await notifier.resolveAcceptRemote(widget.mutation.id);

    if (!mounted) return;
    final state = ref.read(conflictNotifierProvider);
    if (state.error != null) {
      _showError(context, state.error!);
    } else {
      _showSuccess(context, 'Remote version accepted.');
      Navigator.of(context).pop();
    }
  }

  void _editMerged(BuildContext context) {
    // Open the editor on the local file; user merges content manually.
    // On returning, confirm the edit was saved and then resolve.
    Navigator.of(context)
        .push(
          MaterialPageRoute(
            builder: (_) => EditorScreen(filePath: widget.mutation.path),
          ),
        )
        .then((_) => _showResolvedDialog(context));
  }

  Future<void> _showResolvedDialog(BuildContext context) async {
    if (!mounted) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Mark as Resolved?'),
        content: const Text(
          'Did you finish merging the content in the editor?\n\n'
          'Confirming will re-queue this file for push on next sync.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: const Text('Not yet'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: const Text('Confirmed, Resolved'),
          ),
        ],
      ),
    );

    if (confirmed == true && mounted) {
      await ref
          .read(conflictNotifierProvider.notifier)
          .resolveKeepLocal(widget.mutation.id);
      if (mounted) {
        _showSuccess(context, 'Merged version queued for sync.');
        Navigator.of(context).pop();
      }
    }
  }

  void _showError(BuildContext context, String message) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text('Error: $message'),
        backgroundColor: Theme.of(context).colorScheme.error,
      ),
    );
  }

  void _showSuccess(BuildContext context, String message) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(message), duration: const Duration(seconds: 2)),
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final conflictState = ref.watch(conflictNotifierProvider);
    final fileName = widget.mutation.path.split('/').last;
    final hasRemote =
        widget.mutation.conflictFilePath != null &&
        widget.mutation.conflictFilePath!.isNotEmpty;

    return Scaffold(
      appBar: AppBar(
        title: Text(fileName),
        centerTitle: false,
        bottom: TabBar(
          controller: _tabController,
          tabs: const [
            Tab(icon: Icon(Icons.phone_android), text: 'Local'),
            Tab(icon: Icon(Icons.cloud_outlined), text: 'Remote'),
          ],
        ),
      ),
      body: Column(
        children: [
          // Conflict path info banner
          Container(
            width: double.infinity,
            color: theme.colorScheme.errorContainer,
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 8),
            child: Text(
              'Conflict on: ${widget.mutation.path}',
              style: theme.textTheme.bodySmall?.copyWith(
                color: theme.colorScheme.onErrorContainer,
              ),
              maxLines: 2,
              overflow: TextOverflow.ellipsis,
            ),
          ),
          // Tab content
          Expanded(
            child: TabBarView(
              controller: _tabController,
              children: [
                _ContentPane(
                  loading: _loadingLocal,
                  content: _localContent,
                  missingLabel: 'Local file not found.\nSync to restore.',
                ),
                _ContentPane(
                  loading: _loadingRemote,
                  content: _remoteContent,
                  missingLabel: hasRemote
                      ? 'Remote snapshot not downloaded yet.\n'
                            'Sync first to pull conflict file.'
                      : 'No remote snapshot recorded.\n'
                            'The conflict was detected but no conflict\n'
                            'file path was returned by the server.',
                ),
              ],
            ),
          ),
          // Action buttons
          const Divider(height: 1),
          if (conflictState.isLoading)
            const Padding(
              padding: EdgeInsets.all(20),
              child: CircularProgressIndicator(),
            )
          else
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  FilledButton.icon(
                    onPressed: () => _keepLocal(context),
                    icon: const Icon(Icons.phone_android),
                    label: const Text('Keep Local'),
                    style: FilledButton.styleFrom(
                      backgroundColor: theme.colorScheme.primary,
                    ),
                  ),
                  const SizedBox(height: 8),
                  FilledButton.icon(
                    onPressed: () => _acceptRemote(context),
                    icon: const Icon(Icons.cloud_download_outlined),
                    label: const Text('Accept Remote'),
                    style: FilledButton.styleFrom(
                      backgroundColor: theme.colorScheme.secondary,
                    ),
                  ),
                  const SizedBox(height: 8),
                  OutlinedButton.icon(
                    onPressed: () => _editMerged(context),
                    icon: const Icon(Icons.edit_outlined),
                    label: const Text('Edit Merged'),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

/// Displays file content or a helpful placeholder in a scrollable text view.
class _ContentPane extends StatelessWidget {
  final bool loading;
  final String? content;
  final String missingLabel;

  const _ContentPane({
    required this.loading,
    required this.content,
    required this.missingLabel,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    if (loading) {
      return const Center(child: CircularProgressIndicator());
    }

    if (content == null) {
      return Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Text(
            missingLabel,
            textAlign: TextAlign.center,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
        ),
      );
    }

    return SingleChildScrollView(
      padding: const EdgeInsets.all(16),
      child: SelectableText(
        content!,
        style: const TextStyle(
          fontFamily: 'monospace',
          fontSize: 13,
          height: 1.5,
        ),
      ),
    );
  }
}
