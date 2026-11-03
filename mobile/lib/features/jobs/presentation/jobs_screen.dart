import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:jarvis_mobile/core/network/api_exceptions.dart';
import 'package:jarvis_mobile/features/jobs/data/jobs_repository.dart';
import 'package:jarvis_mobile/features/jobs/presentation/run_screen.dart';

const _stages = [
  ('preparing', 'Preparing'),
  ('ready', 'Ready to submit'),
  ('interviewing', 'Interviewing'),
  ('applied', 'Applied'),
  ('shortlist', 'Shortlist'),
  ('new', 'New'),
  ('closed', 'Closed'),
];

/// Job search, driven by the ai-job-search engine on the owner's Claude
/// subscription. Admin devices only (the drawer hides it from guests and the
/// server enforces it).
class JobsScreen extends ConsumerWidget {
  const JobsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final board = ref.watch(jobsBoardProvider);
    final runs = ref.watch(jobsRunsProvider);

    return Scaffold(
      appBar: AppBar(title: const Text('Job Search'), actions: [
        IconButton(
          icon: const Icon(Icons.history),
          tooltip: 'All runs',
          onPressed: () => Navigator.push(context, MaterialPageRoute(builder: (_) => const _RunsPage())),
        ),
      ]),
      floatingActionButton: FloatingActionButton.extended(
        icon: const Icon(Icons.add_link),
        label: const Text('Add posting'),
        onPressed: () => _addPosting(context, ref),
      ),
      body: RefreshIndicator(
        onRefresh: () async {
          ref.invalidate(jobsBoardProvider);
          ref.invalidate(jobsRunsProvider);
          ref.invalidate(jobsStatusProvider);
          await ref.read(jobsBoardProvider.future).catchError((_) => <Map<String, dynamic>>[]);
        },
        child: ListView(
          padding: const EdgeInsets.only(bottom: 96),
          children: [
            const _StatusBanner(),
            _ActionChips(),
            ...runs.maybeWhen(
              data: (list) => [
                for (final r in list.where((r) => r['status'] == 'awaiting_input'))
                  Card(
                    color: Theme.of(context).colorScheme.tertiaryContainer,
                    margin: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
                    child: ListTile(
                      leading: const Icon(Icons.help_outline),
                      title: Text(((r['envelope'] as Map?)?['question'] as String?) ?? 'Claude needs your answer'),
                      subtitle: Text(runTitle(r)),
                      onTap: () => openRun(context, ref, r['id'] as String),
                    ),
                  ),
              ],
              orElse: () => const <Widget>[],
            ),
            board.when(
              loading: () => const Padding(padding: EdgeInsets.all(32), child: Center(child: CircularProgressIndicator())),
              error: (e, _) => const SizedBox.shrink(), // the status banner explains why
              data: (cards) => cards.isEmpty
                  ? const Padding(
                      padding: EdgeInsets.all(32),
                      child: Text('No jobs yet. Tap "Find jobs" to search the job boards.', textAlign: TextAlign.center),
                    )
                  : Column(children: [
                      for (final (stage, label) in _stages)
                        if (cards.any((c) => c['stage'] == stage))
                          _Section(
                            label: label,
                            cards: cards.where((c) => c['stage'] == stage).toList(),
                            collapsed: stage == 'closed' || stage == 'new',
                          ),
                    ]),
            ),
          ],
        ),
      ),
    );
  }
}

Future<void> openRun(BuildContext context, WidgetRef ref, String runId, {String? title}) async {
  await Navigator.push(context, MaterialPageRoute(builder: (_) => RunScreen(runId: runId, title: title)));
  ref.invalidate(jobsBoardProvider);
  ref.invalidate(jobsRunsProvider);
}

Future<void> startRun(BuildContext context, WidgetRef ref, String kind,
    [Map<String, dynamic> args = const {}, String? title]) async {
  try {
    final run = await ref.read(jobsRepositoryProvider).start(kind, args);
    if (context.mounted) await openRun(context, ref, run['id'] as String, title: title);
  } on DioException catch (e) {
    if (context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(mapDioError(e).message)));
    }
  }
}

Future<String?> _prompt(BuildContext context, String title, {String hint = '', int lines = 1}) {
  final ctl = TextEditingController();
  return showDialog<String>(
    context: context,
    builder: (ctx) => AlertDialog(
      title: Text(title),
      content: TextField(
        controller: ctl,
        autofocus: true,
        minLines: lines,
        maxLines: lines == 1 ? 1 : 12,
        decoration: InputDecoration(hintText: hint),
      ),
      actions: [
        TextButton(onPressed: () => Navigator.pop(ctx), child: const Text('Cancel')),
        FilledButton(onPressed: () => Navigator.pop(ctx, ctl.text.trim()), child: const Text('Go')),
      ],
    ),
  );
}

Future<void> _addPosting(BuildContext context, WidgetRef ref) async {
  final posting = await _prompt(context, 'Evaluate a job posting',
      hint: 'Paste the posting URL, or the full posting text', lines: 4);
  if (posting == null || posting.isEmpty || !context.mounted) return;
  // Postings are untrusted text; the engine's posting_guard runs on them first.
  await startRun(context, ref, 'apply', {'posting': posting});
}

Future<void> _open(String url) => launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication);

class _StatusBanner extends ConsumerWidget {
  const _StatusBanner();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(jobsStatusProvider);
    return status.when(
      loading: () => const SizedBox.shrink(),
      error: (e, _) => _banner(context,
          e is DioException ? mapDioError(e).message : '$e', Icons.cloud_off_outlined),
      data: (s) {
        final claude = (s['claude'] as Map?) ?? {};
        if (s['workspace_ok'] != true) {
          return _banner(context, 'The ai-job-search workspace is not mounted. Set JARVIS_JOBS_WORKSPACE.', Icons.folder_off_outlined);
        }
        if (claude['ok'] != true) {
          return _banner(context, 'Claude is not signed in: ${claude['detail'] ?? ''}', Icons.key_off_outlined);
        }
        final running = s['running'] as int? ?? 0;
        final queued = s['queued'] as int? ?? 0;
        if (running + queued == 0) return const SizedBox.shrink();
        return ListTile(
          dense: true,
          leading: const SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)),
          title: Text('$running running · $queued queued'),
        );
      },
    );
  }

  Widget _banner(BuildContext context, String text, IconData icon) => Card(
        margin: const EdgeInsets.all(12),
        color: Theme.of(context).colorScheme.errorContainer,
        child: ListTile(leading: Icon(icon), title: Text(text)),
      );
}

class _ActionChips extends ConsumerWidget {
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    Widget chip(IconData icon, String label, VoidCallback onTap) => ActionChip(
          avatar: Icon(icon, size: 18),
          label: Text(label),
          onPressed: onTap,
        );

    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
      child: Wrap(spacing: 8, runSpacing: 4, children: [
        chip(Icons.travel_explore, 'Find jobs', () => startRun(context, ref, 'scrape')),
        chip(Icons.leaderboard_outlined, 'Rank', () => startRun(context, ref, 'rank')),
        chip(Icons.mark_email_unread_outlined, 'Follow-ups', () => startRun(context, ref, 'followup')),
        chip(Icons.school_outlined, 'Skill gaps', () => startRun(context, ref, 'upskill')),
        chip(Icons.more_horiz, 'More', () => _more(context, ref)),
      ]),
    );
  }

  void _more(BuildContext context, WidgetRef ref) {
    showModalBottomSheet(
      context: context,
      builder: (ctx) {
        void go(String kind, [Map<String, dynamic> args = const {}]) {
          Navigator.pop(ctx);
          startRun(context, ref, kind, args);
        }

        Future<void> ask(String kind, String key, String title, String hint) async {
          Navigator.pop(ctx);
          final text = await _prompt(context, title, hint: hint, lines: key == 'text' ? 4 : 1);
          if (text != null && text.isNotEmpty && context.mounted) startRun(context, ref, kind, {key: text});
        }

        return SafeArea(
          child: ListView(shrinkWrap: true, children: [
            ListTile(leading: const Icon(Icons.search), title: const Text('Find jobs in a focus area'),
                onTap: () => ask('scrape', 'focus', 'Focus area', 'e.g. data science')),
            ListTile(leading: const Icon(Icons.refresh), title: const Text('Re-rank everything'),
                subtitle: const Text('After your profile changed'),
                onTap: () => go('rank', {'options': '--all'})),
            ListTile(leading: const Icon(Icons.inventory_2_outlined), title: const Text('Clean up stale applications'),
                onTap: () => go('stale')),
            ListTile(leading: const Icon(Icons.health_and_safety_outlined), title: const Text('Check job boards'),
                onTap: () => go('portal_health')),
            ListTile(leading: const Icon(Icons.auto_awesome_outlined), title: const Text('Expand profile from GitHub & portfolio'),
                onTap: () => go('expand')),
            ListTile(leading: const Icon(Icons.manage_search), title: const Text('Update search queries'),
                onTap: () => go('setup', {'options': '--section search'})),
            ListTile(leading: const Icon(Icons.chat_outlined), title: const Text('Ask Claude (job workspace)'),
                onTap: () => ask('ask', 'text', 'Ask Claude', 'Anything about your applications or profile')),
          ]),
        );
      },
    );
  }
}

class _Section extends StatelessWidget {
  final String label;
  final List<Map<String, dynamic>> cards;
  final bool collapsed;
  const _Section({required this.label, required this.cards, required this.collapsed});

  @override
  Widget build(BuildContext context) => ExpansionTile(
        initiallyExpanded: !collapsed,
        title: Text('$label (${cards.length})', style: Theme.of(context).textTheme.titleSmall),
        children: [for (final c in cards) _JobCard(card: c)],
      );
}

bool _soon(String? deadline) {
  final d = DateTime.tryParse(deadline ?? '');
  if (d == null) return false;
  final days = d.difference(DateTime.now()).inDays;
  return days >= 0 && days <= 7;
}

class _JobCard extends ConsumerWidget {
  final Map<String, dynamic> card;
  const _JobCard({required this.card});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final score = card['score'];
    final flags = [
      if (_soon(card['deadline'] as String?)) '🔥 ${card['deadline']}',
      if (card['guard'] != null && card['guard'] != 'clean') '⚠ posting flagged',
      if (card['location_verdict'] == 'FLAG' || card['language_gate'] == 'FLAG') '⚠ check fit',
    ];
    return ListTile(
      leading: CircleAvatar(child: Text(score == null ? '–' : '$score')),
      title: Text('${card['title']}'),
      subtitle: Text([
        card['company'],
        if (card['location'] != null) card['location'],
        ...flags,
      ].join(' · ')),
      trailing: card['stage'] == 'preparing' ? const SizedBox(width: 18, height: 18, child: CircularProgressIndicator(strokeWidth: 2)) : null,
      onTap: () => showModalBottomSheet(
        context: context,
        isScrollControlled: true,
        builder: (_) => _JobSheet(card: card, host: context, hostRef: ref),
      ),
    );
  }
}

class _JobSheet extends StatelessWidget {
  final Map<String, dynamic> card;
  // The sheet closes before a run starts, so runs are started from the board's
  // context and ref, which outlive it.
  final BuildContext host;
  final WidgetRef hostRef;
  const _JobSheet({required this.card, required this.host, required this.hostRef});

  @override
  Widget build(BuildContext context) {
    final ref = hostRef;
    final theme = Theme.of(context);
    final stage = card['stage'] as String;
    final company = card['company'] as String;
    final url = card['url'] as String?;
    final links = (card['links'] as Map?) ?? {};
    final app = (card['application'] as Map?)?.cast<String, dynamic>();
    final runId = card['run_id'] as String?;

    void run(String kind, Map<String, dynamic> args, [String? title]) {
      Navigator.pop(context);
      startRun(host, ref, kind, args, title);
    }

    Future<void> outcome() async {
      Navigator.pop(context);
      final note = await _prompt(host, 'What happened?',
          hint: 'e.g. phone screen booked for Friday, or rejected', lines: 3);
      if (note != null && note.isNotEmpty && host.mounted) {
        startRun(host, ref, 'outcome', {'company': '$company ${card['title']}', 'note': note});
      }
    }

    final buttons = <Widget>[
      if (runId != null)
        FilledButton.icon(
          icon: const Icon(Icons.visibility_outlined),
          label: const Text('View progress'),
          onPressed: () {
            Navigator.pop(context);
            openRun(host, ref, runId);
          },
        ),
      if ((stage == 'new' || stage == 'shortlist') && url != null)
        FilledButton.icon(
          icon: const Icon(Icons.rate_review_outlined),
          label: const Text('Evaluate & apply'),
          onPressed: () => run('apply', {'posting': url, 'job_key': card['key']}, 'Apply: $company'),
        ),
      if (stage == 'ready')
        FilledButton.icon(
          icon: const Icon(Icons.send_outlined),
          label: const Text('I submitted it'),
          onPressed: () => run('outcome', {
            'company': '$company ${card['title']}',
            'note': 'I submitted this application today. Move it from drafted to applied and archive what was submitted.',
          }),
        ),
      if (stage == 'applied')
        OutlinedButton.icon(
          icon: const Icon(Icons.mark_email_unread_outlined),
          label: const Text('Follow-up draft'),
          onPressed: () => run('followup', {'target': company}),
        ),
      if (stage == 'applied' || stage == 'interviewing')
        OutlinedButton.icon(icon: const Icon(Icons.edit_note), label: const Text('Record outcome'), onPressed: outcome),
      if (stage == 'applied' || stage == 'interviewing')
        OutlinedButton.icon(
          icon: const Icon(Icons.record_voice_over_outlined),
          label: const Text('Interview prep'),
          onPressed: () => run('interview', {'company': '$company ${card['title']}'}),
        ),
      if (url != null)
        OutlinedButton.icon(icon: const Icon(Icons.open_in_new), label: const Text('Open posting'), onPressed: () => _open(url)),
      if (links['recruiters'] != null)
        OutlinedButton.icon(icon: const Icon(Icons.person_search_outlined), label: const Text('Recruiters'),
            onPressed: () => _open(links['recruiters'] as String)),
      if (links['peers'] != null)
        OutlinedButton.icon(icon: const Icon(Icons.groups_outlined), label: const Text('Team'),
            onPressed: () => _open(links['peers'] as String)),
      if (url != null)
        OutlinedButton.icon(
          icon: const Icon(Icons.school_outlined),
          label: const Text('Skill gaps'),
          onPressed: () => run('upskill', {'url': url}),
        ),
    ];

    return DraggableScrollableSheet(
      expand: false,
      initialChildSize: 0.7,
      maxChildSize: 0.95,
      builder: (context, scroll) => ListView(
        controller: scroll,
        padding: const EdgeInsets.all(16),
        children: [
          Text('${card['title']}', style: theme.textTheme.titleLarge),
          Text([company, if (card['location'] != null) card['location']].join(' · ')),
          const SizedBox(height: 8),
          Wrap(spacing: 6, children: [
            if (card['score'] != null) Chip(label: Text('Fit ${card['score']}${card['verdict'] != null ? ' · ${card['verdict']}' : ''}')),
            if (card['deadline'] != null) Chip(label: Text('Deadline ${card['deadline']}')),
            if (app != null) Chip(label: Text('${app['status']}')),
          ]),
          if (card['guard'] != null && card['guard'] != 'clean')
            Text('⚠ ${card['guard']}', style: TextStyle(color: theme.colorScheme.error)),
          if (card['language_note'] != null) Text('⚠ ${card['language_note']}'),
          for (final s in (card['strengths'] as List? ?? const [])) Text('＋ $s'),
          for (final g in (card['gaps'] as List? ?? const [])) Text('－ $g'),
          const SizedBox(height: 12),
          Wrap(spacing: 8, runSpacing: 8, children: buttons),
          if (app != null) ...[
            const Divider(),
            Text('Documents', style: theme.textTheme.titleSmall),
            if (app['cv_pdf'] != null) ArtifactTile(path: app['cv_pdf'] as String, label: 'CV (PDF)'),
            if (app['cover_pdf'] != null) ArtifactTile(path: app['cover_pdf'] as String, label: 'Cover letter (PDF)'),
            if (app['folder'] != null)
              for (final f in (app['files'] as List)) ArtifactTile(path: '${app['folder']}/$f'),
          ],
        ],
      ),
    );
  }
}

class _RunsPage extends ConsumerWidget {
  const _RunsPage();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final runs = ref.watch(jobsRunsProvider);
    return Scaffold(
      appBar: AppBar(title: const Text('Runs')),
      body: runs.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(child: Text(e is DioException ? mapDioError(e).message : '$e')),
        data: (list) => ListView(children: [
          for (final r in list)
            ListTile(
              title: Text(runTitle(r)),
              subtitle: Text('${r['status']} · ${r['created']}'),
              onTap: () => openRun(context, ref, r['id'] as String),
            ),
        ]),
      ),
    );
  }
}
