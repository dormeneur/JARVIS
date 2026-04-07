import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../data/file_manifest_model.dart';
import 'file_creation_worker.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';

class FileCreationModal extends ConsumerStatefulWidget {
  final List<FileManifestItem> manifest;
  final bool isDryRun;

  const FileCreationModal({super.key, required this.manifest, this.isDryRun = false});

  @override
  ConsumerState<FileCreationModal> createState() => _FileCreationModalState();
}

class _FileCreationModalState extends ConsumerState<FileCreationModal> {
  final Set<String> _selectedPaths = {};
  bool _isWriting = false;

  @override
  void initState() {
    super.initState();
    for (var item in widget.manifest) {
      _selectedPaths.add(item.path);
    }
  }

  void _toggleSelection(String path, bool? value) {
    setState(() {
      if (value == true) {
        _selectedPaths.add(path);
      } else {
        _selectedPaths.remove(path);
      }
    });
  }

  Future<void> _executeCreation() async {
    final selectedItems = widget.manifest.where((item) => _selectedPaths.contains(item.path)).toList();
    if (selectedItems.isEmpty) return;

    setState(() {
      _isWriting = true;
    });

    try {
      final worker = ref.read(fileCreationWorkerProvider);
      await worker.executeCreation(selectedItems);

      if (mounted) {
        Navigator.of(context).pop(true);
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text('Successfully created ${selectedItems.length} items.'),
            backgroundColor: Colors.green,
          ),
        );
      }
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(
            content: Text('Failed to create files: $e'),
            backgroundColor: Theme.of(context).colorScheme.error,
          ),
        );
      }
    } finally {
      if (mounted) {
        setState(() {
          _isWriting = false;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final explorerState = ref.watch(directoryEntriesProvider);
    final existingPaths = <String>{};
    
    // Check local database for current path conflicts recursively.
    // For a deeper check, we can rely on catching them as conflicts, but we provide UI hints locally.
    if (!explorerState.isLoading && !explorerState.hasError) {
       for (var entry in explorerState.value ?? []) {
          existingPaths.add(entry.path);
       }
    }

    final theme = Theme.of(context);

    return AlertDialog(
      title: Row(
        children: [
          const Text('Confirm File Creation'),
          if (widget.isDryRun) ...[
            const SizedBox(width: 8),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
              decoration: BoxDecoration(
                color: theme.colorScheme.tertiaryContainer,
                borderRadius: BorderRadius.circular(4),
              ),
              child: Text(
                'PREVIEW ONLY',
                style: TextStyle(
                  fontSize: 10,
                  fontWeight: FontWeight.bold,
                  color: theme.colorScheme.onTertiaryContainer,
                ),
              ),
            ),
          ],
        ],
      ),
      content: SizedBox(
        width: double.maxFinite,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'The AI wants to create the following items. Uncheck any items you do not want to create.',
              style: theme.textTheme.bodyMedium,
            ),
            const SizedBox(height: 16),
            Flexible(
              child: ListView.builder(
                shrinkWrap: true,
                itemCount: widget.manifest.length,
                itemBuilder: (ctx, idx) {
                  final item = widget.manifest[idx];
                  final isConflict = existingPaths.contains(item.path);
                  
                  return Card(
                    margin: const EdgeInsets.symmetric(vertical: 4),
                    child: Theme(
                      data: theme.copyWith(dividerColor: Colors.transparent),
                      child: ExpansionTile(
                        leading: Checkbox(
                          value: _selectedPaths.contains(item.path),
                          onChanged: (val) => _toggleSelection(item.path, val),
                        ),
                        title: Row(
                          children: [
                            Icon(
                              item.type == 'directory' ? Icons.folder : Icons.insert_drive_file,
                              color: isConflict ? Colors.orange : theme.colorScheme.primary,
                              size: 20,
                            ),
                            const SizedBox(width: 8),
                            Expanded(child: Text(item.path)),
                          ],
                        ),
                        subtitle: isConflict
                            ? const Text('Warning: File already exists', style: TextStyle(color: Colors.orange, fontSize: 12))
                            : null,
                        children: [
                          if (item.type == 'file' && item.content.isNotEmpty)
                            Container(
                              width: double.infinity,
                              padding: const EdgeInsets.all(12),
                              color: theme.colorScheme.surfaceContainerHighest,
                              child: Text(
                                item.content,
                                style: const TextStyle(fontFamily: 'monospace', fontSize: 12),
                              ),
                            )
                        ],
                      ),
                    ),
                  );
                },
              ),
            ),
          ],
        ),
      ),
      actions: [
        if (!widget.isDryRun)
          TextButton(
            onPressed: _isWriting ? null : () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
        FilledButton(
          onPressed: _isWriting || (!widget.isDryRun && _selectedPaths.isEmpty) 
            ? null 
            : (widget.isDryRun ? () => Navigator.of(context).pop(true) : _executeCreation),
          child: _isWriting 
            ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
            : Text(widget.isDryRun ? 'Close' : 'Create ${_selectedPaths.length} Items'),
        ),
      ],
    );
  }
}
