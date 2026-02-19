import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';
import 'package:jarvis_mobile/shared/utils/hash_utils.dart';
import 'package:drift/drift.dart' as drift;
import 'package:jarvis_mobile/core/storage/app_database.dart';

/// Markdown editor — loads from local mirror, saves locally, updates SQLite.
class EditorScreen extends ConsumerStatefulWidget {
  final String filePath;

  const EditorScreen({super.key, required this.filePath});

  @override
  ConsumerState<EditorScreen> createState() => _EditorScreenState();
}

class _EditorScreenState extends ConsumerState<EditorScreen> {
  late TextEditingController _controller;
  bool _loading = true;
  bool _dirty = false;
  bool _saving = false;
  String? _error;
  FileEntry? _entry;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController();
    _loadFile();
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Future<void> _loadFile() async {
    final repo = ref.read(explorerRepositoryProvider);
    final entry = await repo.getEntry(widget.filePath);

    if (entry == null || entry.localPath == null) {
      setState(() {
        _loading = false;
        _error = 'File not found in local mirror.';
      });
      return;
    }

    final file = File(entry.localPath!);
    if (!file.existsSync()) {
      setState(() {
        _loading = false;
        _error = 'Local file missing. Sync again.';
      });
      return;
    }

    final content = await file.readAsString();
    setState(() {
      _entry = entry;
      _controller.text = content;
      _loading = false;
    });

    _controller.addListener(() {
      if (!_dirty) setState(() => _dirty = true);
    });
  }

  Future<void> _save() async {
    if (_entry == null || !_dirty) return;

    setState(() => _saving = true);

    try {
      final content = _controller.text;
      final file = File(_entry!.localPath!);
      await file.writeAsString(content);

      // Update hash and last_modified in SQLite
      final hash = sha256String(content);
      final mtime = nowUtcIso8601();
      final db = ref.read(appDatabaseProvider);

      await db.upsertEntry(
        FileCacheEntriesCompanion(
          path: drift.Value(_entry!.path),
          name: drift.Value(_entry!.name),
          type: const drift.Value('file'),
          sizeBytes: drift.Value(content.length),
          lastModified: drift.Value(mtime),
          contentHash: drift.Value(hash),
          localPath: drift.Value(_entry!.localPath),
          lastSynced: drift.Value(_entry!.lastSynced),
        ),
      );

      setState(() {
        _dirty = false;
        _saving = false;
        _entry = _entry!.copyWith(contentHash: hash, lastModified: mtime);
      });

      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('Saved locally'),
            duration: Duration(seconds: 1),
          ),
        );
      }
    } catch (e) {
      setState(() {
        _saving = false;
        _error = 'Save failed: $e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fileName = widget.filePath.split('/').last;

    return Scaffold(
      appBar: AppBar(
        title: Text(fileName),
        actions: [
          if (_dirty)
            IconButton(
              onPressed: _saving ? null : _save,
              icon: _saving
                  ? const SizedBox(
                      height: 18,
                      width: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.save),
            ),
        ],
      ),
      body: _loading
          ? const Center(child: CircularProgressIndicator())
          : _error != null
          ? Center(
              child: Padding(
                padding: const EdgeInsets.all(32),
                child: Text(
                  _error!,
                  textAlign: TextAlign.center,
                  style: TextStyle(color: theme.colorScheme.error),
                ),
              ),
            )
          : Padding(
              padding: const EdgeInsets.all(16),
              child: TextField(
                controller: _controller,
                maxLines: null,
                expands: true,
                textAlignVertical: TextAlignVertical.top,
                style: const TextStyle(fontFamily: 'monospace', fontSize: 14),
                decoration: const InputDecoration(
                  border: InputBorder.none,
                  hintText: 'Start writing…',
                ),
              ),
            ),
    );
  }
}
