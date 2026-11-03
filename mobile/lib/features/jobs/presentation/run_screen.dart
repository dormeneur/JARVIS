import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/api_exceptions.dart';
import 'package:jarvis_mobile/features/jobs/data/jobs_repository.dart';
import 'package:jarvis_mobile/features/viewer/presentation/file_viewer_screen.dart';

const _kindLabels = {
  'scrape': 'Find new jobs',
  'portal_health': 'Job board health check',
  'rank': 'Rank jobs',
  'apply': 'Application',
  'outcome': 'Record outcome',
  'followup': 'Follow-up draft',
  'stale': 'Stale applications',
  'interview': 'Interview prep',
  'upskill': 'Skill gaps',
  'expand': 'Expand profile',
  'setup': 'Profile setup',
  'ask': 'Ask Claude',
};

String runTitle(Map<String, dynamic> run) => _kindLabels[run['kind']] ?? '${run['kind']}';

/// Live view of one engine run: progress steps while Claude works, then the
/// question it stopped on (answer buttons) or its final report.
class RunScreen extends ConsumerStatefulWidget {
  final String runId;
  final String? title;
  const RunScreen({super.key, required this.runId, this.title});

  @override
  ConsumerState<RunScreen> createState() => _RunScreenState();
}

class _RunScreenState extends ConsumerState<RunScreen> {
  final _steps = <String>[];
  final _answer = TextEditingController();
  Map<String, dynamic>? _run;
  StreamSubscription? _sub;
  String? _error;
  bool _busy = false;

  JobsRepository get _repo => ref.read(jobsRepositoryProvider);

  @override
  void initState() {
    super.initState();
    _follow();
  }

  @override
  void dispose() {
    _sub?.cancel();
    _answer.dispose();
    super.dispose();
  }

  void _follow() {
    _sub?.cancel();
    _steps.clear();
    _sub = _repo.events(widget.runId).listen(
      (e) {
        final type = e['type'];
        if (type == 'step' || type == 'denied') {
          setState(() => _steps.add(type == 'denied' ? '⛔ Blocked: ${e['text']}' : e['text'] as String));
        } else if (type == 'answer') {
          setState(() => _steps.add('You: ${e['text']}'));
        } else if (type == 'status') {
          _refresh();
        }
      },
      onError: (e) => setState(() => _error = e is DioException ? mapDioError(e).message : '$e'),
      onDone: _refresh,
    );
  }

  Future<void> _refresh() async {
    try {
      final run = await _repo.run(widget.runId);
      if (mounted) setState(() => _run = run);
    } on DioException catch (e) {
      if (mounted) setState(() => _error = mapDioError(e).message);
    }
  }

  Future<void> _do(Future<void> Function() action) async {
    setState(() => _busy = true);
    try {
      await action();
      _follow();
    } on DioException catch (e) {
      _snack(mapDioError(e).message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  void _snack(String msg) =>
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(msg)));

  Future<void> _sendAnswer(String text) => _do(() async {
        await _repo.answer(widget.runId, text);
        _answer.clear();
      });

  @override
  Widget build(BuildContext context) {
    final run = _run;
    final status = run?['status'] as String? ?? 'queued';
    final envelope = (run?['envelope'] as Map?)?.cast<String, dynamic>();
    final active = status == 'queued' || status == 'running';
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: Text(widget.title ?? (run == null ? 'Run' : runTitle(run))),
        actions: [
          if (active)
            TextButton(onPressed: _busy ? null : () => _do(() => _repo.action(widget.runId, 'cancel')), child: const Text('Cancel')),
        ],
      ),
      body: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          _StatusRow(status: status, usage: run?['usage'] as Map?),
          if (_error != null) Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
          const SizedBox(height: 8),
          for (final s in _steps)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 2),
              child: Row(children: [
                const Icon(Icons.check_circle_outline, size: 16),
                const SizedBox(width: 8),
                Expanded(child: Text(s, style: theme.textTheme.bodySmall)),
              ]),
            ),
          if (active) const Padding(padding: EdgeInsets.all(16), child: LinearProgressIndicator()),
          if (run?['integrity'] == 'corrupt')
            _Warning(
              text: 'The tracker or job list looks damaged after this run.',
              action: 'Restore',
              onTap: () => _do(() => _repo.action(widget.runId, 'restore')),
            ),
          if ((run?['profile_changes'] as List?)?.isNotEmpty ?? false)
            _Warning(
              text: 'Claude updated your job profile: ${(run!['profile_changes'] as List).join(', ')}. '
                  'Undo restores the files from before this run.',
              action: 'Undo',
              onTap: () => _do(() => _repo.action(widget.runId, 'restore')),
            ),
          if (status == 'failed' || status == 'cancelled') ...[
            Text(run?['error'] as String? ?? status, style: TextStyle(color: theme.colorScheme.error)),
            const SizedBox(height: 8),
            FilledButton.tonal(
              onPressed: _busy ? null : () => _do(() => _repo.action(widget.runId, 'retry')),
              child: const Text('Retry'),
            ),
          ],
          if (envelope != null && !active) ...[
            const Divider(),
            if (status == 'awaiting_input') ...[
              Text(envelope['question'] as String? ?? 'Claude needs your input',
                  style: theme.textTheme.titleMedium),
              const SizedBox(height: 8),
              Wrap(spacing: 8, runSpacing: 8, children: [
                for (final o in (envelope['options'] as List? ?? const []))
                  FilledButton(onPressed: _busy ? null : () => _sendAnswer('$o'), child: Text('$o')),
              ]),
            ],
            const SizedBox(height: 12),
            MarkdownBody(data: envelope['report_markdown'] as String? ?? '', selectable: true),
            for (final a in (envelope['artifacts'] as List? ?? const []))
              ArtifactTile(path: (a as Map)['path'] as String),
          ],
          if (!active && run != null) ...[
            const SizedBox(height: 16),
            TextField(
              controller: _answer,
              minLines: 1,
              maxLines: 5,
              decoration: InputDecoration(
                hintText: status == 'awaiting_input' ? 'Or type your own answer…' : 'Ask a follow-up in this session…',
                suffixIcon: IconButton(
                  icon: const Icon(Icons.send),
                  onPressed: _busy || run['session_id'] == null
                      ? null
                      : () => _answer.text.trim().isEmpty ? null : _sendAnswer(_answer.text),
                ),
              ),
            ),
          ],
        ],
      ),
    );
  }
}

class _StatusRow extends StatelessWidget {
  final String status;
  final Map? usage;
  const _StatusRow({required this.status, this.usage});

  @override
  Widget build(BuildContext context) {
    final tokens = usage == null ? 0 : (usage!['input_tokens'] as num) + (usage!['output_tokens'] as num);
    return Row(children: [
      Chip(label: Text(status.replaceAll('_', ' '))),
      const Spacer(),
      if (tokens > 0) Text('${(tokens / 1000).toStringAsFixed(1)}k tokens', style: Theme.of(context).textTheme.bodySmall),
    ]);
  }
}

class _Warning extends StatelessWidget {
  final String text;
  final String action;
  final VoidCallback onTap;
  const _Warning({required this.text, required this.action, required this.onTap});

  @override
  Widget build(BuildContext context) => Card(
        color: Theme.of(context).colorScheme.errorContainer,
        child: ListTile(
          leading: const Icon(Icons.warning_amber_outlined),
          title: Text(text),
          trailing: TextButton(onPressed: onTap, child: Text(action)),
        ),
      );
}

/// A produced file: PDFs open in the viewer; everything can be copied as text.
class ArtifactTile extends ConsumerWidget {
  final String path;
  final String? label;
  const ArtifactTile({super.key, required this.path, this.label});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final isPdf = path.toLowerCase().endsWith('.pdf');
    return ListTile(
      contentPadding: EdgeInsets.zero,
      leading: Icon(isPdf ? Icons.picture_as_pdf_outlined : Icons.description_outlined),
      title: Text(label ?? path.split('/').last),
      onTap: () => openArtifact(context, ref, path),
      trailing: IconButton(
        icon: const Icon(Icons.copy),
        tooltip: 'Copy text',
        onPressed: () => copyArtifact(context, ref, path),
      ),
    );
  }
}

Future<void> copyArtifact(BuildContext context, WidgetRef ref, String path) async {
  final messenger = ScaffoldMessenger.of(context);
  try {
    final text = await ref.read(jobsRepositoryProvider).artifactText(path);
    await Clipboard.setData(ClipboardData(text: text));
    messenger.showSnackBar(SnackBar(content: Text('Copied ${text.length} characters')));
  } on DioException catch (e) {
    messenger.showSnackBar(SnackBar(content: Text(mapDioError(e).message)));
  }
}

Future<void> openArtifact(BuildContext context, WidgetRef ref, String path) async {
  final navigator = Navigator.of(context);
  final messenger = ScaffoldMessenger.of(context);
  final repo = ref.read(jobsRepositoryProvider);
  try {
    if (path.toLowerCase().endsWith('.pdf')) {
      final file = await repo.download(path);
      navigator.push(MaterialPageRoute(
          builder: (_) => FileViewerScreen(localPath: file.path, fileName: path.split('/').last)));
    } else {
      final text = await repo.artifactText(path);
      navigator.push(MaterialPageRoute(builder: (_) => _TextPage(title: path.split('/').last, text: text)));
    }
  } on DioException catch (e) {
    messenger.showSnackBar(SnackBar(content: Text(mapDioError(e).message)));
  }
}

class _TextPage extends StatelessWidget {
  final String title;
  final String text;
  const _TextPage({required this.title, required this.text});

  @override
  Widget build(BuildContext context) => Scaffold(
        appBar: AppBar(title: Text(title), actions: [
          IconButton(
            icon: const Icon(Icons.copy),
            onPressed: () {
              Clipboard.setData(ClipboardData(text: text));
              ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Copied')));
            },
          ),
        ]),
        body: SingleChildScrollView(
          padding: const EdgeInsets.all(16),
          child: title.endsWith('.md') ? MarkdownBody(data: text, selectable: true) : SelectableText(text),
        ),
      );
}
