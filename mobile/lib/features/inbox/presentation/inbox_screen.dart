import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/inbox/data/inbox_repository.dart';

/// Things JARVIS wants to save (facts it picked up in chat, later mail drafts), each waiting for a
/// yes. Nothing is written until the user approves; the server skips a proposal whose file changed.
class InboxScreen extends ConsumerStatefulWidget {
  const InboxScreen({super.key});

  @override
  ConsumerState<InboxScreen> createState() => _InboxScreenState();
}

class _InboxScreenState extends ConsumerState<InboxScreen> {
  final _busy = <String>{};

  Future<void> _decide(Proposal p, bool approve) async {
    if (_busy.contains(p.id)) return;
    setState(() => _busy.add(p.id));
    final messenger = ScaffoldMessenger.of(context);
    try {
      final result = await ref.read(inboxRepositoryProvider).decide(p.id, approve);
      messenger.showSnackBar(SnackBar(content: Text(result.outcome)));
    } catch (_) {
      messenger.showSnackBar(const SnackBar(content: Text('Could not reach your server. Try again.')));
    } finally {
      if (mounted) setState(() => _busy.remove(p.id));
      ref.invalidate(inboxProvider);
      ref.invalidate(inboxCountProvider);
    }
  }

  @override
  Widget build(BuildContext context) {
    final inbox = ref.watch(inboxProvider);
    return Scaffold(
      appBar: AppBar(title: const Text('Inbox')),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(inboxProvider);
          await ref.read(inboxProvider.future).catchError((_) => <Proposal>[]);
        },
        child: inbox.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (_, _) => ListView(children: const [
            Padding(
              padding: EdgeInsets.all(32),
              child: Text('Connect to your server to review proposals.', textAlign: TextAlign.center),
            ),
          ]),
          data: (items) => items.isEmpty
              ? ListView(children: const [
                  Padding(
                    padding: EdgeInsets.all(32),
                    child: Text('Nothing is waiting for you.', textAlign: TextAlign.center),
                  ),
                ])
              : ListView(
                  padding: const EdgeInsets.all(12),
                  children: [for (final p in items) _ProposalCard(p, busy: _busy.contains(p.id), onDecide: _decide)],
                ),
        ),
      ),
    );
  }
}

class _ProposalCard extends StatelessWidget {
  const _ProposalCard(this.p, {required this.busy, required this.onDecide});

  final Proposal p;
  final bool busy;
  final Future<void> Function(Proposal, bool) onDecide;

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(children: [
              Expanded(child: Text(p.title, style: Theme.of(context).textTheme.titleMedium)),
              if (p.source.isNotEmpty) Chip(label: Text(p.source), visualDensity: VisualDensity.compact),
            ]),
            const SizedBox(height: 8),
            MarkdownBody(data: p.detail),
            const SizedBox(height: 12),
            Row(mainAxisAlignment: MainAxisAlignment.end, children: [
              TextButton(onPressed: busy ? null : () => onDecide(p, false), child: const Text('Reject')),
              const SizedBox(width: 8),
              FilledButton(onPressed: busy ? null : () => onDecide(p, true), child: const Text('Approve')),
            ]),
          ],
        ),
      ),
    );
  }
}
