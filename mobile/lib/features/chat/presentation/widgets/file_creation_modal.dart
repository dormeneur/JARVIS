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
  final Set<int> _selectedIndices = {};
  late List<FileManifestItem> _manifestItems;
  bool _isWriting = false;

  @override
  void initState() {
    super.initState();
    _manifestItems = List.from(widget.manifest);
    for (int i = 0; i < _manifestItems.length; i++) {
      _selectedIndices.add(i);
    }
  }

  void _toggleSelection(int index, bool? value) {
    setState(() {
      if (value == true) {
        _selectedIndices.add(index);
      } else {
        _selectedIndices.remove(index);
      }
    });
  }

  Future<void> _editPath(int index) async {
    final currentItem = _manifestItems[index];
    final newPath = await showDialog<String>(
      context: context,
      builder: (context) => _EditPathDialog(initialPath: currentItem.path),
    );

    if (newPath != null && newPath.trim().isNotEmpty && newPath != currentItem.path) {
      setState(() {
        _manifestItems[index] = currentItem.copyWith(path: newPath.trim());
      });
    }
  }

  Future<void> _executeCreation() async {
    final selectedItems = _manifestItems.asMap().entries
        .where((entry) => _selectedIndices.contains(entry.key))
        .map((entry) => entry.value)
        .toList();
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
                itemCount: _manifestItems.length,
                itemBuilder: (ctx, idx) {
                  final item = _manifestItems[idx];
                  final isConflict = existingPaths.contains(item.path);
                  
                  return Card(
                    margin: const EdgeInsets.symmetric(vertical: 4),
                    child: Theme(
                      data: theme.copyWith(dividerColor: Colors.transparent),
                      child: ExpansionTile(
                        leading: Checkbox(
                          value: _selectedIndices.contains(idx),
                          onChanged: (val) => _toggleSelection(idx, val),
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
                            IconButton(
                              icon: const Icon(Icons.edit, size: 18),
                              onPressed: () => _editPath(idx),
                              tooltip: 'Edit Path',
                              padding: EdgeInsets.zero,
                              constraints: const BoxConstraints(),
                            ),
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
          onPressed: _isWriting || (!widget.isDryRun && _selectedIndices.isEmpty) 
            ? null 
            : (widget.isDryRun ? () => Navigator.of(context).pop(true) : _executeCreation),
          child: _isWriting 
            ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
            : Text(widget.isDryRun ? 'Close' : 'Create ${_selectedIndices.length} Items'),
        ),
      ],
    );
  }
}

class _EditPathDialog extends ConsumerStatefulWidget {
  final String initialPath;
  const _EditPathDialog({required this.initialPath});

  @override
  ConsumerState<_EditPathDialog> createState() => _EditPathDialogState();
}

class _EditPathDialogState extends ConsumerState<_EditPathDialog> {
  TextEditingController? _autocompleteDirController;
  late TextEditingController _nameController;
  late String _initialDir;
  List<String> _allDirs = [];

  @override
  void initState() {
    super.initState();
    String dirPath = '';
    String fileName = widget.initialPath;
    
    int lastSlash = widget.initialPath.lastIndexOf('/');
    int lastBackslash = widget.initialPath.lastIndexOf('\\');
    int lastSeparator = lastSlash > lastBackslash ? lastSlash : lastBackslash;

    if (lastSeparator != -1) {
      dirPath = widget.initialPath.substring(0, lastSeparator);
      fileName = widget.initialPath.substring(lastSeparator + 1);
    }

    _initialDir = dirPath;
    _nameController = TextEditingController(text: fileName);
    _loadDirs();
  }

  Future<void> _loadDirs() async {
    try {
      final repo = ref.read(explorerRepositoryProvider);
      final allFiles = await repo.getAllFiles();
      final dirs = <String>{};
      for (var file in allFiles) {
        if (file.type == 'directory') {
          dirs.add(file.path);
        } else {
          final path = file.path;
          int lastSlash = path.lastIndexOf('/');
          if (lastSlash != -1) {
             dirs.add(path.substring(0, lastSlash));
          }
        }
      }
      if (mounted) {
        setState(() {
          _allDirs = dirs.toList()..sort();
        });
      }
    } catch (e) {
      // Ignore if there's an issue loading dirs, autocomplete will just be empty
    }
  }

  @override
  void dispose() {
    _nameController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Edit File Details'),
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Autocomplete<String>(
              initialValue: TextEditingValue(text: _initialDir),
              optionsBuilder: (TextEditingValue textEditingValue) {
                if (textEditingValue.text.isEmpty) {
                  return _allDirs;
                }
                return _allDirs.where((String option) {
                  return option.toLowerCase().contains(textEditingValue.text.toLowerCase());
                });
              },
              fieldViewBuilder: (context, controller, focusNode, onFieldSubmitted) {
                _autocompleteDirController = controller;
                return TextField(
                  controller: controller,
                  focusNode: focusNode,
                  decoration: const InputDecoration(
                    labelText: 'Directory Path',
                    hintText: 'e.g. lib/features/chat',
                    border: OutlineInputBorder(),
                    prefixIcon: Icon(Icons.folder_outlined),
                  ),
                );
              },
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _nameController,
              decoration: const InputDecoration(
                labelText: 'File Name',
                hintText: 'e.g. my_file.dart',
                border: OutlineInputBorder(),
                prefixIcon: Icon(Icons.insert_drive_file_outlined),
              ),
              autofocus: true,
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            final dir = _autocompleteDirController?.text.trim() ?? _initialDir;
            final name = _nameController.text.trim();
            if (name.isEmpty) return; 
            
            String newPath = name;
            if (dir.isNotEmpty) {
              String sep = dir.contains('\\') ? '\\' : '/';
              if (dir.endsWith('/') || dir.endsWith('\\')) {
                newPath = '$dir$name';
              } else {
                newPath = '$dir$sep$name';
              }
            }
            Navigator.of(context).pop(newPath);
          },
          child: const Text('Save'),
        ),
      ],
    );
  }
}

