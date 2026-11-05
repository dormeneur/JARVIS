import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/career/data/career_repository.dart';
import 'package:jarvis_mobile/features/career/domain/career_profile.dart';
import 'package:jarvis_mobile/features/career/presentation/career_widgets.dart';
import 'package:jarvis_mobile/features/career/presentation/profile_controller.dart';

/// Picks a resume, saves it to Profile/resume/, has the server read it and adds what it found to
/// the profile (never overwriting). Returns the extraction, or null if the user cancelled.
Future<ResumeExtraction?> importResume(BuildContext context, WidgetRef ref) async {
  final picked = await FilePicker.platform.pickFiles(type: FileType.custom, allowedExtensions: const ['pdf', 'docx'], withData: true);
  final file = picked?.files.singleOrNull;
  if (file == null || file.bytes == null) return null;
  final repo = ref.read(careerRepositoryProvider);
  final ctl = ref.read(profileControllerProvider);
  final geo = await ref.read(geoProvider.future);
  final path = await repo.upload(CareerRepository.resumeFolder, file.name, file.bytes!);
  final found = await repo.extract(path);
  final p = ctl.profile!;
  mergeExtraction(p, found.profile, dials: geo.dials, countryCode: geo.codeOf, regionCode: geo.regionOf);
  final resumes = p['resumes'] as List;
  if (!resumes.any((r) => r is Map && r['path'] == path)) {
    final first = resumes.isEmpty;
    final entry = <String, dynamic>{'path': path, 'label': '', 'countries': <String>[], 'default': first};
    resumes.add(entry);
    if (!first && context.mounted) {
      final country = await showDialog<bool>(
        context: context,
        builder: (d) => AlertDialog(
          title: const Text('Which country is this resume for?'),
          content: const Text('JARVIS uploads it for jobs in that country, and your default resume everywhere else.'),
          actions: [
            TextButton(onPressed: () => Navigator.pop(d, false), child: const Text('Any country')),
            FilledButton(onPressed: () => Navigator.pop(d, true), child: const Text('Pick a country')),
          ],
        ),
      );
      if (country == true && context.mounted) {
        final c = await pickCountry(context, geo);
        if (c != null) {
          entry['countries'] = [c.code];
          entry['label'] = c.name;
        }
      }
    }
  }
  ctl.changed();
  await ctl.flush();
  return found;
}

/// The one-time setup (spec §6.1): resume, details, work authorization, preferences, portal
/// password. Each step saves as it goes; reopening resumes at the first unfinished step.
class SetupWizard extends ConsumerStatefulWidget {
  const SetupWizard({super.key, required this.onFinished});

  final VoidCallback onFinished;

  @override
  ConsumerState<SetupWizard> createState() => _SetupWizardState();
}

class _SetupWizardState extends ConsumerState<SetupWizard> {
  static const _titles = ['Upload your resume', 'Review your details', 'Work authorization', 'Work preferences', 'Job portal password'];
  int? _step;
  String? _problem;
  final _scroll = ScrollController();

  @override
  void dispose() {
    _scroll.dispose();
    super.dispose();
  }

  String? _blocker(Json p) {
    if (_step != 1) return null;
    final c = p['contact'] as Map;
    if (str(c['first_name']).isEmpty || str(c['last_name']).isEmpty) return 'Add your first and last name';
    if (!RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$').hasMatch(str(c['email']))) return 'Add a valid email';
    return null;
  }

  Future<void> _next(ProfileController ctl) async {
    final p = ctl.profile!;
    // Shown above the buttons, not in a snackbar that would cover them.
    setState(() => _problem = _blocker(p));
    if (_problem != null) return;
    markStep(p, setupSteps[_step!]);
    if (_step == setupSteps.length - 1) {
      (p['setup'] as Json)['finished_at'] = DateTime.now().toIso8601String();
      ctl.changed();
      await ctl.flush();
      widget.onFinished();
      return;
    }
    ctl.changed();
    setState(() => _step = _step! + 1);
    _scroll.jumpTo(0);
  }

  @override
  Widget build(BuildContext context) {
    final ctl = ref.watch(profileControllerProvider);
    final p = ctl.profile!;
    final done = (p['setup'] as Map)['completed'] as List;
    final step = _step ??= setupSteps.indexWhere((s) => !done.contains(s)).clamp(0, setupSteps.length - 1);
    final body = switch (step) {
      0 => const _ResumeStep(),
      1 => const Column(children: [ContactCard(), AddressCard(), ExperienceSection(), EducationSection(), SkillsSection()]),
      2 => const AuthEditor(),
      3 => const PrefsEditor(),
      _ => const PortalPasswordCard(),
    };
    return Scaffold(
      appBar: AppBar(title: const Text('Set up job applications')),
      body: Column(children: [
        LinearProgressIndicator(value: (step + 1) / setupSteps.length),
        Padding(
          padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
          child: Row(children: [
            Expanded(child: Text(_titles[step], style: Theme.of(context).textTheme.titleLarge)),
            Text('${step + 1} of ${setupSteps.length}', style: Theme.of(context).textTheme.bodySmall),
          ]),
        ),
        if (ctl.saveError != null)
          Padding(padding: const EdgeInsets.symmetric(horizontal: 16), child: Text(ctl.saveError!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
        Expanded(child: ListView(controller: _scroll, padding: const EdgeInsets.all(16), children: [body])),
        if (_problem != null)
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 0),
            child: Text(_problem!, style: TextStyle(color: Theme.of(context).colorScheme.error)),
          ),
        SafeArea(
          top: false,
          child: Padding(
            padding: const EdgeInsets.fromLTRB(16, 8, 16, 12),
            child: Row(children: [
              if (step > 0)
                TextButton(
                  onPressed: () => setState(() {
                    _step = step - 1;
                    _problem = null;
                  }),
                  child: const Text('Back'),
                ),
              const Spacer(),
              FilledButton(
                onPressed: () => _next(ctl),
                child: Text(step == setupSteps.length - 1 ? "Finish" : step == 0 && (p['resumes'] as List).isEmpty ? 'Skip' : 'Next'),
              ),
            ]),
          ),
        ),
      ]),
    );
  }
}

class _ResumeStep extends ConsumerStatefulWidget {
  const _ResumeStep();

  @override
  ConsumerState<_ResumeStep> createState() => _ResumeStepState();
}

class _ResumeStepState extends ConsumerState<_ResumeStep> {
  bool _busy = false;
  String? _error;
  ResumeExtraction? _found;
  bool _showUnverified = false;

  Future<void> _pick() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final found = await importResume(context, ref);
      if (found != null) setState(() => _found = found);
    } catch (e) {
      setState(() => _error = e is CareerException ? e.message : 'Could not upload the resume. Is your server running?');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final resumes = ref.watch(profileControllerProvider).profile!['resumes'] as List;
    final found = _found;
    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      const Text('JARVIS reads your resume and fills in your profile. You check everything on the next screen.'),
      const SizedBox(height: 20),
      if (_busy) ...[
        const Center(child: CircularProgressIndicator()),
        const SizedBox(height: 12),
        const Text('Reading your resume… this can take a minute.', textAlign: TextAlign.center),
      ] else ...[
        if (found != null) ...[
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(foundLine(found.counts), style: theme.textTheme.titleMedium),
                if (found.engine == 'local')
                  Text('Read on your PC by the local model (the job engine is off).', style: theme.textTheme.bodySmall),
                if (found.unverified.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  Text("${found.unverified.length} items couldn't be confirmed from the text, so they were left out."),
                  TextButton(
                    onPressed: () => setState(() => _showUnverified = !_showUnverified),
                    child: Text(_showUnverified ? 'Hide' : 'Show'),
                  ),
                  if (_showUnverified) for (final u in found.unverified) Text('• $u', style: theme.textTheme.bodySmall),
                ],
              ]),
            ),
          ),
          const SizedBox(height: 12),
        ],
        for (final r in resumes.cast<Map>())
          ListTile(
            leading: const Icon(Icons.description_outlined),
            title: Text(str(r['path']).split('/').last),
            subtitle: Text(r['default'] == true ? 'Default resume' : 'For ${str(r['label']).isEmpty ? 'any country' : r['label']}'),
          ),
        FilledButton.tonalIcon(
          icon: const Icon(Icons.upload_file),
          label: Text(resumes.isEmpty ? 'Choose resume (PDF or Word)' : 'Add another resume'),
          onPressed: _pick,
        ),
        if (resumes.isEmpty)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Text('No resume handy? Press Skip and enter your details by hand.', style: theme.textTheme.bodySmall, textAlign: TextAlign.center),
          ),
      ],
      if (_error != null) Padding(padding: const EdgeInsets.only(top: 12), child: Text(_error!, style: TextStyle(color: theme.colorScheme.error))),
    ]);
  }
}

/// The password JARVIS uses to create job-portal accounts (Workday...). Kept in this phone's
/// secure storage only; the PC keeps its own copy (spec §8).
class PortalPasswordCard extends ConsumerStatefulWidget {
  const PortalPasswordCard({super.key});

  @override
  ConsumerState<PortalPasswordCard> createState() => _PortalPasswordCardState();
}

class _PortalPasswordCardState extends ConsumerState<PortalPasswordCard> {
  final _text = TextEditingController();
  bool _hidden = true;
  bool? _saved;
  String? _problem;

  @override
  void initState() {
    super.initState();
    ref.read(careerRepositoryProvider).portalPassword().then((v) {
      if (mounted) setState(() => _saved = v != null && v.isNotEmpty);
    });
  }

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  Future<void> _save(String value) async {
    final problem = portalPasswordProblem(value);
    setState(() => _problem = problem);
    if (problem != null) return;
    await ref.read(careerRepositoryProvider).setPortalPassword(value);
    if (mounted) setState(() => _saved = true);
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return SectionCard(
      title: 'One password for job portals',
      subtitle: 'Workday and some other job sites make you create an account for each company. '
          'JARVIS uses this password for those accounts. Use it nowhere else.',
      children: [
        if (_saved == true) const ListTile(contentPadding: EdgeInsets.zero, leading: Icon(Icons.lock_outline), title: Text('A password is saved on this phone')),
        TextField(
          controller: _text,
          obscureText: _hidden,
          decoration: InputDecoration(
            labelText: _saved == true ? 'Replace it' : 'Password',
            border: const OutlineInputBorder(),
            errorText: _problem,
            suffixIcon: IconButton(icon: Icon(_hidden ? Icons.visibility : Icons.visibility_off), onPressed: () => setState(() => _hidden = !_hidden)),
          ),
          onChanged: (_) => setState(() {}),
          onSubmitted: _save,
        ),
        const SizedBox(height: 8),
        Wrap(spacing: 8, children: [
          OutlinedButton(
            onPressed: () {
              _text.text = generatePortalPassword();
              setState(() => _hidden = false);
            },
            child: const Text('Generate one'),
          ),
          OutlinedButton(
            onPressed: _text.text.isEmpty ? null : () => Clipboard.setData(ClipboardData(text: _text.text)),
            child: const Text('Copy'),
          ),
          FilledButton(onPressed: () => _save(_text.text), child: const Text('Save')),
        ]),
        const SizedBox(height: 8),
        Text('Kept only on this phone, never sent to your server or any AI. Your PC keeps its own copy.',
            style: theme.textTheme.bodySmall),
      ],
    );
  }
}
