import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart';
import 'package:jarvis_mobile/features/relay/relay_repository.dart';

/// Send a job to the PC from anywhere: Jarvis on the PC picks it up within a minute (while it's
/// running and not in private mode), runs it as a background job, and the result comes back here
/// with a notification.
class RelayScreen extends ConsumerStatefulWidget {
  const RelayScreen({super.key});

  @override
  ConsumerState<RelayScreen> createState() => _RelayScreenState();
}

class _RelayScreenState extends ConsumerState<RelayScreen> {
  final _brief = TextEditingController();
  List<RelayJob>? _jobs;
  bool _offline = false;
  bool _sending = false;
  Timer? _poll;

  @override
  void initState() {
    super.initState();
    _load();
    _poll = Timer.periodic(const Duration(seconds: 5), (_) => _load());
  }

  @override
  void dispose() {
    _poll?.cancel();
    _brief.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final jobs = await ref.read(relayRepositoryProvider).recent();
      if (mounted) {
        setState(() {
          _jobs = jobs;
          _offline = false;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _offline = true);
    }
  }

  Future<void> _send() async {
    final brief = _brief.text.trim();
    if (brief.length < 3 || _sending) return;
    setState(() => _sending = true);
    final messenger = ScaffoldMessenger.of(context);
    try {
      final job = await ref.read(relayRepositoryProvider).send(brief);
      _brief.clear();
      await RelayWatcher.askPermission();
      await ref.read(relayWatcherProvider).track(job.id);
      messenger.showSnackBar(const SnackBar(content: Text("Sent. I'll let you know when your PC is done.")));
      await _load();
    } on DioException catch (e) {
      final detail = e.response?.data is Map ? (e.response!.data as Map)['detail'] : null;
      messenger.showSnackBar(SnackBar(
        content: Text(e.response?.statusCode == 422 && detail is String ? detail : "Couldn't reach your PC. Try again."),
      ));
    } finally {
      if (mounted) setState(() => _sending = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final muted = Theme.of(context).textTheme.bodySmall;
    return Scaffold(
      appBar: AppBar(title: const Text('On my PC')),
      body: RefreshIndicator(
        onRefresh: _load,
        child: ListView(
          padding: const EdgeInsets.all(12),
          children: [
            TextField(
              controller: _brief,
              minLines: 2,
              maxLines: 6,
              maxLength: 2000,
              decoration: const InputDecoration(
                hintText: 'What should your PC do? e.g. "Summarise the PDFs in Downloads into a note"',
                border: OutlineInputBorder(),
              ),
            ),
            Row(
              children: [
                Expanded(child: Text('Runs on your PC with Claude, like a job from the notch. No passwords here.', style: muted)),
                const SizedBox(width: 12),
                FilledButton.icon(
                  onPressed: _sending ? null : _send,
                  icon: _sending ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2)) : const Icon(Icons.send),
                  label: const Text('Send'),
                ),
              ],
            ),
            const SizedBox(height: 16),
            if (_offline && _jobs == null)
              const Padding(padding: EdgeInsets.all(24), child: Text('Connect to your PC to see jobs.', textAlign: TextAlign.center))
            else if (_jobs == null)
              const Center(child: Padding(padding: EdgeInsets.all(24), child: CircularProgressIndicator()))
            else if (_jobs!.isEmpty)
              const Padding(padding: EdgeInsets.all(24), child: Text('Nothing sent yet.', textAlign: TextAlign.center))
            else
              for (final job in _jobs!) _JobCard(job),
          ],
        ),
      ),
    );
  }
}

class _JobCard extends StatelessWidget {
  const _JobCard(this.job);

  final RelayJob job;

  @override
  Widget build(BuildContext context) {
    final (label, color) = switch (job.status) {
      'queued' => ('Waiting for your PC', Colors.grey),
      'running' => ('Running', Colors.blue),
      'finished' => ('Done', Colors.green),
      _ => ("Didn't finish", Colors.orange),
    };
    final when = job.updated == null ? '' : DateFormat('d MMM, HH:mm').format(job.updated!);
    return Card(
      margin: const EdgeInsets.only(bottom: 10),
      child: ExpansionTile(
        title: Text(job.brief, maxLines: 2, overflow: TextOverflow.ellipsis),
        subtitle: Text([label, if (job.from.isNotEmpty) 'from ${job.from}', when].where((s) => s.isNotEmpty).join(' · '), style: TextStyle(color: color)),
        leading: job.status == 'running'
            ? const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2))
            : Icon(job.status == 'finished' ? Icons.check_circle : job.status == 'failed' ? Icons.error_outline : Icons.schedule, color: color),
        childrenPadding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
        expandedCrossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (job.result != null && job.result!.isNotEmpty) MarkdownBody(data: job.result!, selectable: true) else Text(job.brief),
          if (job.status == 'queued')
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                'Your PC picks this up within a minute while Jarvis is running there and private mode is off.',
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ),
        ],
      ),
    );
  }
}
