import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/career/data/career_repository.dart';
import 'package:jarvis_mobile/features/career/domain/career_profile.dart';
import 'package:jarvis_mobile/features/career/presentation/career_widgets.dart';
import 'package:jarvis_mobile/features/career/presentation/profile_controller.dart';
import 'package:jarvis_mobile/features/career/presentation/setup_wizard.dart';
import 'package:jarvis_mobile/features/editor/presentation/editor_screen.dart';

/// The Career space (14-Career-Copilot.md §6): the setup wizard the first time, then the profile.
// ponytail: Jobs / Applications / Auto / Inbox tabs arrive with spec Phase 2; until then the
// space is the profile itself.
class CareerScreen extends ConsumerWidget {
  const CareerScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    if (ctl.loadError != null) {
      return Scaffold(
        appBar: AppBar(title: const Text('Career')),
        body: Center(
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            const Text('Connect to your server to open your career profile.'),
            const SizedBox(height: 12),
            FilledButton(onPressed: ctl.load, child: const Text('Try again')),
          ]),
        ),
      );
    }
    if (ctl.loading) return const Scaffold(body: Center(child: CircularProgressIndicator()));
    if (!setupFinished(ctl.profile!)) return SetupWizard(onFinished: () {});
    return const CareerProfileScreen();
  }
}

class CareerProfileScreen extends ConsumerWidget {
  const CareerProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final geo = ref.watch(geoProvider).valueOrNull;
    final p = ctl.profile!;
    final country = geo?.byCode(str((p['address'] as Map)['country']));
    final (percent, missing) = completeness(p, hasRegions: country?.regions.isNotEmpty ?? false);
    return DefaultTabController(
      length: 4,
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Career profile'),
          bottom: const TabBar(tabs: [Tab(text: 'Resume'), Tab(text: 'Personal'), Tab(text: 'Files'), Tab(text: 'Answers')]),
        ),
        body: Column(children: [
          _Completeness(percent: percent, missing: missing),
          if (ctl.saveError != null)
            Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: Text(ctl.saveError!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
          Expanded(
            child: TabBarView(children: [
              ListView(padding: const EdgeInsets.all(16), children: const [
                _UpdateFromResume(),
                ExperienceSection(),
                EducationSection(),
                SkillsSection(),
                ProjectsSection(),
                ProjectsSection(kind: 'certificates'),
              ]),
              ListView(padding: const EdgeInsets.all(16), children: [for (final s in _personal) editorFor(s)]),
              const _FilesTab(),
              const _AnswersTab(),
            ]),
          ),
        ]),
      ),
    );
  }
}

const _personal = ['contact', 'address', 'links', 'summary', 'languages', 'auth', 'prefs', 'salary', 'targets', 'background'];

/// The editor that fixes one completeness item (also the Personal tab's sections, in order).
Widget editorFor(String section) => switch (section) {
      'contact' => const ContactCard(),
      'address' => const AddressCard(),
      'experience' => const ExperienceSection(),
      'education' => const EducationSection(),
      'skills' => const SkillsSection(),
      'summary' => const _SummaryCard(),
      'links' => const _LinksCard(),
      'languages' => const _LanguagesCard(),
      'auth' => const AuthEditor(),
      'prefs' => const PrefsEditor(),
      'salary' => const _SalaryCard(),
      'targets' => const _TargetsCard(),
      _ => const BackgroundEditor(),
    };

class _Completeness extends StatelessWidget {
  const _Completeness({required this.percent, required this.missing});

  final int percent;
  final List<Missing> missing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
      child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Row(children: [
          Text('$percent% complete', style: theme.textTheme.titleMedium),
          const Spacer(),
          if (missing.isNotEmpty)
            TextButton(
              onPressed: () => showModalBottomSheet<void>(
                context: context,
                builder: (sheet) => SafeArea(
                  child: ListView(shrinkWrap: true, children: [
                    const ListTile(title: Text('Fill in the rest')),
                    for (final m in missing)
                      ListTile(
                        leading: const Icon(Icons.radio_button_unchecked),
                        title: Text(m.label),
                        trailing: Text('+${m.weight}%'),
                        onTap: () {
                          Navigator.pop(sheet);
                          Navigator.of(context).push(MaterialPageRoute(
                            builder: (_) => Scaffold(
                              appBar: AppBar(title: Text(m.label)),
                              body: ListView(padding: const EdgeInsets.all(16), children: [editorFor(m.section)]),
                            ),
                          ));
                        },
                      ),
                  ]),
                ),
              ),
              child: Text('Fill in the rest (${missing.length})'),
            ),
        ]),
        const SizedBox(height: 4),
        LinearProgressIndicator(value: percent / 100),
      ]),
    );
  }
}

class _UpdateFromResume extends ConsumerStatefulWidget {
  const _UpdateFromResume();

  @override
  ConsumerState<_UpdateFromResume> createState() => _UpdateFromResumeState();
}

class _UpdateFromResumeState extends ConsumerState<_UpdateFromResume> {
  bool _busy = false;

  Future<void> _run() async {
    final messenger = ScaffoldMessenger.of(context);
    final before = _count();
    setState(() => _busy = true);
    try {
      final found = await importResume(context, ref);
      if (found != null) {
        final added = _count() - before;
        messenger.showSnackBar(SnackBar(content: Text('${foundLine(found.counts)}. Added $added new ${added == 1 ? 'item' : 'items'}; nothing you entered was changed.')));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(content: Text(e is CareerException ? e.message : 'Could not upload the resume.')));
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  int _count() {
    final p = ref.read(profileControllerProvider).profile!;
    return (p['experience'] as List).length + (p['education'] as List).length + (p['projects'] as List).length +
        (p['certificates'] as List).length + skillCount(p);
  }

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: _busy
            ? const Row(children: [SizedBox(width: 20, height: 20, child: CircularProgressIndicator(strokeWidth: 2)), SizedBox(width: 12), Text('Reading your resume…')])
            : OutlinedButton.icon(icon: const Icon(Icons.upload_file), label: const Text('Update from a resume'), onPressed: _run),
      );
}

class _SummaryCard extends ConsumerWidget {
  const _SummaryCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    return SectionCard(title: 'Summary', subtitle: 'Two or three lines about you, for forms that ask.', children: [
      MapField(ctl.profile!, 'summary', 'Summary', lines: 6, onChanged: ctl.changed),
    ]);
  }
}

class _LinksCard extends ConsumerWidget {
  const _LinksCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final links = ctl.profile!['links'] as Json;
    return SectionCard(title: 'Links', children: [
      MapField(links, 'linkedin', 'LinkedIn', keyboard: TextInputType.url, onChanged: ctl.changed),
      MapField(links, 'github', 'GitHub', keyboard: TextInputType.url, onChanged: ctl.changed),
      MapField(links, 'website', 'Website or portfolio', keyboard: TextInputType.url, onChanged: ctl.changed),
    ]);
  }
}

/// Spoken languages, as "English (fluent)" chips; the level is optional.
class _LanguagesCard extends ConsumerWidget {
  const _LanguagesCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final p = ctl.profile!;
    final shown = [
      for (final l in (p['languages'] as List).cast<Map>()) str(l['level']).isEmpty ? str(l['name']) : '${str(l['name'])} (${str(l['level'])})',
    ];
    return SectionCard(title: 'Languages you speak', subtitle: 'e.g. "English (fluent)"', children: [
      ChipsEditor(
        values: shown,
        hint: 'Add a language',
        onChanged: () {
          p['languages'] = [
            for (final s in shown)
              if (RegExp(r'^(.*?)\s*\((.*)\)$').firstMatch(s) case final m?) {'name': m[1]!.trim(), 'level': m[2]!.trim()} else {'name': s, 'level': ''},
          ];
          ctl.changed();
        },
      ),
    ]);
  }
}

class _SalaryCard extends ConsumerWidget {
  const _SalaryCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    return SectionCard(title: 'Expected salary', subtitle: 'Used when a form asks for your expectation.', children: [
      SalaryFields(salary: (ctl.profile!['preferences'] as Json)['salary'] as Json, onChanged: ctl.changed),
    ]);
  }
}

/// What you're looking for (spec §4.2 targets): feeds matching and Auto later.
class _TargetsCard extends ConsumerWidget {
  const _TargetsCard();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final t = ctl.profile!['targets'] as Json;
    final remote = t['remote'] as List;

    Widget mode(String key, String label) => FilterChip(
          label: Text(label),
          selected: remote.contains(key),
          onSelected: (on) {
            on ? remote.add(key) : remote.remove(key);
            ctl.changed();
          },
        );

    return SectionCard(title: "What you're looking for", children: [
      const Text('Roles'),
      const SizedBox(height: 4),
      ChipsEditor(values: t['roles'] as List, onChanged: ctl.changed, hint: 'e.g. Backend Engineer'),
      const SizedBox(height: 12),
      const Text('Locations'),
      const SizedBox(height: 4),
      ChipsEditor(values: t['locations'] as List, onChanged: ctl.changed, hint: 'e.g. Bengaluru, Remote India, Bangkok'),
      const SizedBox(height: 12),
      Wrap(spacing: 8, children: [mode('remote', 'Remote'), mode('hybrid', 'Hybrid'), mode('onsite', 'Onsite')]),
      const SizedBox(height: 12),
      SalaryFields(salary: t['salary_floor'] as Json, onChanged: ctl.changed, label: 'Lowest salary you\'d accept'),
      const Text('Companies to skip'),
      const SizedBox(height: 4),
      ChipsEditor(values: t['avoid_companies'] as List, onChanged: ctl.changed, hint: 'Add a company'),
    ]);
  }
}

class _FilesTab extends ConsumerStatefulWidget {
  const _FilesTab();

  @override
  ConsumerState<_FilesTab> createState() => _FilesTabState();
}

class _FilesTabState extends ConsumerState<_FilesTab> {
  static const folders = [('Profile/certificates', 'Certificates'), ('Profile/transcripts', 'Transcripts')];
  late Future<List<List<String>>> _files = _load();

  Future<List<List<String>>> _load() => Future.wait([for (final (f, _) in folders) ref.read(careerRepositoryProvider).listFiles(f)]);

  Future<void> _upload(String folder) async {
    final file = (await FilePicker.platform.pickFiles(withData: true))?.files.singleOrNull;
    if (file?.bytes == null) return;
    await ref.read(careerRepositoryProvider).upload(folder, file!.name, file.bytes!);
    setState(() => _files = _load());
  }

  @override
  Widget build(BuildContext context) {
    final ctl = ref.watch(profileControllerProvider);
    final resumes = (ctl.profile!['resumes'] as List).cast<Json>();
    return ListView(padding: const EdgeInsets.all(16), children: [
      SectionCard(title: 'Resumes', subtitle: 'The default goes to every job; a country resume to jobs in that country.', children: [
        for (final r in resumes)
          ListTile(
            contentPadding: EdgeInsets.zero,
            leading: const Icon(Icons.description_outlined),
            title: Text(str(r['path']).split('/').last),
            subtitle: Text([if (r['default'] == true) 'Default', if (str(r['label']).isNotEmpty) 'For ${r['label']}'].join(' · ')),
            trailing: PopupMenuButton<String>(
              onSelected: (v) {
                if (v == 'default') {
                  for (final other in resumes) {
                    other['default'] = identical(other, r);
                  }
                } else if (v == 'remove') {
                  (ctl.profile!['resumes'] as List).remove(r);
                }
                ctl.changed();
              },
              itemBuilder: (_) => const [
                PopupMenuItem(value: 'default', child: Text('Make default')),
                PopupMenuItem(value: 'remove', child: Text('Stop using it (the file stays in your vault)')),
              ],
            ),
          ),
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(icon: const Icon(Icons.add), label: const Text('Add a resume'), onPressed: () => importResume(context, ref)),
        ),
      ]),
      FutureBuilder<List<List<String>>>(
        future: _files,
        builder: (_, snap) => Column(children: [
          for (var i = 0; i < folders.length; i++)
            SectionCard(title: folders[i].$2, children: [
              for (final f in snap.data?[i] ?? const <String>[])
                ListTile(contentPadding: EdgeInsets.zero, leading: const Icon(Icons.insert_drive_file_outlined), title: Text(f.split('/').last)),
              Align(
                alignment: Alignment.centerLeft,
                child: TextButton.icon(icon: const Icon(Icons.upload_file), label: const Text('Upload'), onPressed: () => _upload(folders[i].$1)),
              ),
            ]),
        ]),
      ),
    ]);
  }
}

class _AnswersTab extends StatelessWidget {
  const _AnswersTab();

  @override
  Widget build(BuildContext context) {
    void open(String path) => Navigator.of(context).push(MaterialPageRoute(builder: (_) => EditorScreen(filePath: path)));
    return ListView(padding: const EdgeInsets.all(16), children: [
      const Text('Answers JARVIS reuses on forms. Edit or delete anything that is out of date.'),
      const SizedBox(height: 12),
      ListTile(
        leading: const Icon(Icons.edit_note),
        title: const Text('Standard answers'),
        subtitle: const Text('Your written answers to common questions'),
        onTap: () => open('Profile/standard-answers.md'),
      ),
      ListTile(
        leading: const Icon(Icons.history),
        title: const Text('Remembered form answers'),
        subtitle: const Text('What you picked on past forms'),
        onTap: () => open('Profile/form-memory.json'),
      ),
    ]);
  }
}
