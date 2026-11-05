import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/career/data/career_repository.dart';
import 'package:jarvis_mobile/features/career/domain/career_profile.dart';
import 'package:jarvis_mobile/features/career/presentation/profile_controller.dart';

/// The editors shared by the setup wizard and the profile screen. Each edits the profile map in
/// place and calls [ProfileController.changed], which saves shortly after.

class SectionCard extends StatelessWidget {
  const SectionCard({super.key, required this.title, this.subtitle, required this.children, this.trailing});

  final String title;
  final String? subtitle;
  final List<Widget> children;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Card(
      margin: const EdgeInsets.only(bottom: 16),
      child: Padding(
        padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
        child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
          Row(children: [
            Expanded(child: Text(title, style: theme.textTheme.titleMedium)),
            ?trailing,
          ]),
          if (subtitle != null) ...[
            const SizedBox(height: 2),
            Text(subtitle!, style: theme.textTheme.bodySmall),
          ],
          const SizedBox(height: 8),
          ...children,
        ]),
      ),
    );
  }
}

/// A text field bound to `map[key]`.
class MapField extends StatefulWidget {
  const MapField(this.map, this.field, this.label,
      {super.key, required this.onChanged, this.keyboard, this.hint, this.lines = 1, this.capitalization = TextCapitalization.none});

  final Map<String, dynamic> map;
  final String field;
  final String label;
  final VoidCallback onChanged;
  final TextInputType? keyboard;
  final String? hint;
  final int lines;
  final TextCapitalization capitalization;

  @override
  State<MapField> createState() => _MapFieldState();
}

class _MapFieldState extends State<MapField> {
  late final _text = TextEditingController(text: widget.map[widget.field]?.toString() ?? '');

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: TextField(
          controller: _text,
          keyboardType: widget.lines > 1 ? TextInputType.multiline : widget.keyboard,
          textCapitalization: widget.capitalization,
          minLines: widget.lines > 1 ? 2 : 1,
          maxLines: widget.lines,
          decoration: InputDecoration(labelText: widget.label, hintText: widget.hint, border: const OutlineInputBorder(), isDense: true),
          onChanged: (v) {
            widget.map[widget.field] = v.trim();
            widget.onChanged();
          },
        ),
      );
}

/// Skills (or roles, locations…) as chips with ×, plus a box to add more. Enter adds; a pasted
/// "a, b, c" adds three; a duplicate (ignoring case) is refused.
class ChipsEditor extends StatefulWidget {
  const ChipsEditor({super.key, required this.values, required this.onChanged, this.hint = 'Add and press Enter'});

  final List values;
  final VoidCallback onChanged;
  final String hint;

  @override
  State<ChipsEditor> createState() => _ChipsEditorState();
}

class _ChipsEditorState extends State<ChipsEditor> {
  final _text = TextEditingController();

  @override
  void dispose() {
    _text.dispose();
    super.dispose();
  }

  void _add(String raw) {
    final have = {for (final v in widget.values) v.toString().toLowerCase()};
    var refused = false;
    for (final part in raw.split(',')) {
      final value = part.trim();
      if (value.isEmpty) continue;
      if (have.contains(value.toLowerCase())) {
        refused = true;
        continue;
      }
      widget.values.add(value);
      have.add(value.toLowerCase());
    }
    _text.clear();
    setState(() {});
    widget.onChanged();
    if (refused) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Already added'), duration: Duration(seconds: 1)));
    }
  }

  @override
  Widget build(BuildContext context) => Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
        Wrap(spacing: 6, runSpacing: 6, children: [
          for (final v in List.of(widget.values))
            InputChip(
              label: Text(v.toString()),
              onDeleted: () {
                setState(() => widget.values.remove(v));
                widget.onChanged();
              },
            ),
        ]),
        const SizedBox(height: 8),
        TextField(
          controller: _text,
          decoration: InputDecoration(hintText: widget.hint, isDense: true, border: const OutlineInputBorder()),
          onSubmitted: _add,
          onChanged: (v) {
            if (v.endsWith(',')) _add(v);
          },
        ),
      ]);
}

/// A yes/no question; nothing is selected until the user answers (a guess is never stored).
class YesNoRow extends StatelessWidget {
  const YesNoRow({super.key, required this.label, required this.value, required this.onChanged});

  final String label;
  final bool? value;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 6),
        child: Row(children: [
          Expanded(child: Text(label)),
          const SizedBox(width: 8),
          SegmentedButton<bool>(
            segments: const [ButtonSegment(value: true, label: Text('Yes')), ButtonSegment(value: false, label: Text('No'))],
            selected: {?value},
            emptySelectionAllowed: true,
            showSelectedIcon: false,
            onSelectionChanged: (s) {
              if (s.isNotEmpty) onChanged(s.first);
            },
          ),
        ]),
      );
}

/// A searchable country picker (bottom sheet over the bundled ISO list).
Future<Country?> pickCountry(BuildContext context, Geo geo) => showModalBottomSheet<Country>(
      context: context,
      isScrollControlled: true,
      builder: (_) => _CountrySheet(geo),
    );

class _CountrySheet extends StatefulWidget {
  const _CountrySheet(this.geo);

  final Geo geo;

  @override
  State<_CountrySheet> createState() => _CountrySheetState();
}

class _CountrySheetState extends State<_CountrySheet> {
  var _query = '';

  @override
  Widget build(BuildContext context) {
    final q = _query.toLowerCase();
    final shown = widget.geo.countries.where((c) => q.isEmpty || c.name.toLowerCase().contains(q) || c.code.toLowerCase() == q).toList();
    return SafeArea(
      child: SizedBox(
        height: MediaQuery.of(context).size.height * 0.75,
        child: Column(children: [
          Padding(
            padding: const EdgeInsets.all(12),
            child: TextField(
              autofocus: true,
              decoration: const InputDecoration(prefixIcon: Icon(Icons.search), hintText: 'Search countries', border: OutlineInputBorder()),
              onChanged: (v) => setState(() => _query = v.trim()),
            ),
          ),
          Expanded(
            child: ListView.builder(
              itemCount: shown.length,
              itemBuilder: (_, i) => ListTile(
                title: Text(shown[i].name),
                trailing: Text(shown[i].dial),
                onTap: () => Navigator.of(context).pop(shown[i]),
              ),
            ),
          ),
        ]),
      ),
    );
  }
}

class ContactCard extends ConsumerWidget {
  const ContactCard({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.read(profileControllerProvider);
    final c = ctl.profile!['contact'] as Json;
    final geo = ref.watch(geoProvider).valueOrNull;
    return SectionCard(title: 'Contact details', children: [
      Row(children: [
        Expanded(child: MapField(c, 'first_name', 'First name', onChanged: ctl.changed, capitalization: TextCapitalization.words)),
        const SizedBox(width: 8),
        Expanded(child: MapField(c, 'last_name', 'Last name', onChanged: ctl.changed, capitalization: TextCapitalization.words)),
      ]),
      Row(children: [
        Expanded(child: MapField(c, 'middle_name', 'Middle name', onChanged: ctl.changed, capitalization: TextCapitalization.words)),
        const SizedBox(width: 8),
        Expanded(child: MapField(c, 'preferred_name', 'Preferred name', onChanged: ctl.changed, capitalization: TextCapitalization.words)),
      ]),
      MapField(c, 'email', 'Email', onChanged: ctl.changed, keyboard: TextInputType.emailAddress),
      Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Padding(
          padding: const EdgeInsets.only(right: 8),
          child: OutlinedButton(
            style: OutlinedButton.styleFrom(minimumSize: const Size(72, 48)),
            onPressed: geo == null
                ? null
                : () async {
                    final picked = await pickCountry(context, geo);
                    if (picked == null) return;
                    c['phone_country'] = picked.dial;
                    ctl.changed();
                  },
            child: Text(str(c['phone_country']).isEmpty ? 'Code' : str(c['phone_country'])),
          ),
        ),
        Expanded(child: MapField(c, 'phone', 'Phone number', onChanged: ctl.changed, keyboard: TextInputType.phone)),
      ]),
    ]);
  }
}

class AddressCard extends ConsumerWidget {
  const AddressCard({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final a = ctl.profile!['address'] as Json;
    final geo = ref.watch(geoProvider).valueOrNull;
    final country = geo?.byCode(str(a['country']));
    return SectionCard(title: 'Where you live', children: [
      MapField(a, 'line1', 'Street address', onChanged: ctl.changed),
      MapField(a, 'line2', 'Apartment, floor (optional)', onChanged: ctl.changed),
      Row(children: [
        Expanded(child: MapField(a, 'city', 'City', onChanged: ctl.changed, capitalization: TextCapitalization.words)),
        const SizedBox(width: 8),
        Expanded(child: MapField(a, 'postal', 'ZIP / Postal code', onChanged: ctl.changed)),
      ]),
      Padding(
        padding: const EdgeInsets.only(bottom: 10),
        child: OutlinedButton.icon(
          key: const Key('country-picker'),
          icon: const Icon(Icons.public),
          label: Align(alignment: Alignment.centerLeft, child: Text(country?.name ?? 'Country')),
          onPressed: geo == null
              ? null
              : () async {
                  final picked = await pickCountry(context, geo);
                  if (picked == null || picked.code == a['country']) return;
                  a
                    ..['country'] = picked.code
                    ..['country_name'] = picked.name
                    ..['region'] = ''
                    ..['region_name'] = '';
                  final phone = ctl.profile!['contact'] as Json;
                  if (str(phone['phone_country']).isEmpty) phone['phone_country'] = picked.dial;
                  ctl.changed();
                },
        ),
      ),
      if (country != null && country.regions.isNotEmpty)
        DropdownButtonFormField<String>(
          key: ValueKey('region-${country.code}'),
          initialValue: country.regions.any((r) => r.$1 == a['region']) ? a['region'] as String : null,
          isExpanded: true,
          decoration: const InputDecoration(labelText: 'State / Province', border: OutlineInputBorder(), isDense: true),
          items: [for (final (code, name) in country.regions) DropdownMenuItem(value: code, child: Text(name))],
          onChanged: (code) {
            a
              ..['region'] = code
              ..['region_name'] = country.regions.firstWhere((r) => r.$1 == code).$2;
            ctl.changed();
          },
        )
      else if (country != null)
        MapField(a, 'region_name', 'State / Province (if any)', key: ValueKey('region-text-${country.code}'), onChanged: ctl.changed),
    ]);
  }
}

String _period(Map e) {
  String fmt(Object? v) => str(v).isEmpty ? '?' : str(v);
  final end = e['current'] == true || e['enrolled'] == true ? 'now' : fmt(e['end']);
  return '${fmt(e['start'])} – $end';
}

/// Work experience: a headline, one tile per role (tap to edit), and Add.
class ExperienceSection extends ConsumerWidget {
  const ExperienceSection({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final p = ctl.profile!;
    final roles = p['experience'] as List;
    return SectionCard(title: 'Work experience', subtitle: experienceHeadline(p), children: [
      for (final role in roles.cast<Json>())
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text('${str(role['title'])} · ${str(role['company'])}'),
          subtitle: Text(_period(role)),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => _editRole(context, ctl, role),
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(
          icon: const Icon(Icons.add),
          label: const Text('Add work experience'),
          onPressed: () => _editRole(context, ctl, null),
        ),
      ),
    ]);
  }

  Future<void> _editRole(BuildContext context, ProfileController ctl, Json? role) async {
    final list = ctl.profile!['experience'] as List;
    final draft = Map<String, dynamic>.from(role ?? {'id': nextId(list, 'e'), 'source': 'user', 'bullets': <String>[]});
    draft['bullets'] = List<String>.from((draft['bullets'] as List?) ?? const []);
    final result = await Navigator.of(context).push<String>(MaterialPageRoute(
      builder: (_) => ItemEditor(
        title: role == null ? 'Add role' : 'Edit role',
        item: draft,
        canDelete: role != null,
        fields: const [('title', 'Job title'), ('company', 'Company'), ('location', 'Location'),
          ('employment_type', 'Type (full time, internship…)'), ('start', 'Start (YYYY-MM)')],
        endField: ('end', 'End (YYYY-MM)', 'current', 'I currently work here'),
        bulletsField: 'bullets',
      ),
    ));
    if (result == null) return;
    if (result == 'delete') {
      list.remove(role);
    } else {
      draft['source'] = 'user';
      if (role == null) {
        list.add(draft);
      } else {
        list[list.indexOf(role)] = draft;
      }
    }
    ctl.changed();
  }
}

class EducationSection extends ConsumerWidget {
  const EducationSection({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final list = ctl.profile!['education'] as List;
    return SectionCard(title: 'Education', children: [
      for (final e in list.cast<Json>())
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text([str(e['degree']), str(e['major'])].where((s) => s.isNotEmpty).join(', ')),
          subtitle: Text('${str(e['school'])} · ${_period(e)}${str(e['gpa']).isEmpty ? '' : ' · GPA ${str(e['gpa'])}'}'),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => _edit(context, ctl, e),
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(icon: const Icon(Icons.add), label: const Text('Add education'), onPressed: () => _edit(context, ctl, null)),
      ),
    ]);
  }

  Future<void> _edit(BuildContext context, ProfileController ctl, Json? item) async {
    final list = ctl.profile!['education'] as List;
    final draft = Map<String, dynamic>.from(item ?? {'id': nextId(list, 'd')});
    final result = await Navigator.of(context).push<String>(MaterialPageRoute(
      builder: (_) => ItemEditor(
        title: item == null ? 'Add education' : 'Edit education',
        item: draft,
        canDelete: item != null,
        fields: const [('degree', 'Degree'), ('major', 'Major / field'), ('school', 'University / school'),
          ('gpa', 'GPA'), ('gpa_scale', 'GPA out of (e.g. 10 or 4)'), ('honors', 'Honors'), ('start', 'Start (YYYY-MM)')],
        endField: ('end', 'End (YYYY-MM)', 'enrolled', 'Currently enrolled'),
      ),
    ));
    if (result == null) return;
    if (result == 'delete') {
      list.remove(item);
    } else if (item == null) {
      list.add(draft);
    } else {
      list[list.indexOf(item)] = draft;
    }
    ctl.changed();
  }
}

class ProjectsSection extends ConsumerWidget {
  const ProjectsSection({super.key, this.kind = 'projects'});

  /// 'projects' or 'certificates'.
  final String kind;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final list = ctl.profile![kind] as List;
    final projects = kind == 'projects';
    return SectionCard(title: projects ? 'Projects' : 'Certificates', children: [
      for (final e in list.cast<Json>())
        ListTile(
          contentPadding: EdgeInsets.zero,
          title: Text(str(e['name'])),
          subtitle: Text(projects ? str(e['url']) : [str(e['issuer']), str(e['date'])].where((s) => s.isNotEmpty).join(' · ')),
          trailing: const Icon(Icons.chevron_right),
          onTap: () => _edit(context, ctl, e),
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(
            icon: const Icon(Icons.add), label: Text(projects ? 'Add project' : 'Add certificate'), onPressed: () => _edit(context, ctl, null)),
      ),
    ]);
  }

  Future<void> _edit(BuildContext context, ProfileController ctl, Json? item) async {
    final list = ctl.profile![kind] as List;
    final projects = kind == 'projects';
    final draft = Map<String, dynamic>.from(item ?? {'id': nextId(list, projects ? 'p' : 'c')});
    if (projects) draft['bullets'] = List<String>.from((draft['bullets'] as List?) ?? const []);
    final result = await Navigator.of(context).push<String>(MaterialPageRoute(
      builder: (_) => ItemEditor(
        title: projects ? 'Project' : 'Certificate',
        item: draft,
        canDelete: item != null,
        fields: projects ? const [('name', 'Name'), ('url', 'Link')] : const [('name', 'Name'), ('issuer', 'Issued by'), ('date', 'Date (YYYY-MM)'), ('url', 'Link')],
        bulletsField: projects ? 'bullets' : null,
      ),
    ));
    if (result == null) return;
    if (result == 'delete') {
      list.remove(item);
    } else if (item == null) {
      list.add(draft);
    } else {
      list[list.indexOf(item)] = draft;
    }
    ctl.changed();
  }
}

/// Edits one role / school / project on a copy; pops 'save', 'delete' or null (cancelled).
class ItemEditor extends StatefulWidget {
  const ItemEditor({super.key, required this.title, required this.item, required this.fields, this.endField, this.bulletsField, this.canDelete = false});

  final String title;
  final Json item;
  final List<(String, String)> fields;

  /// (end key, label, "still here" key, its label)
  final (String, String, String, String)? endField;
  final String? bulletsField;
  final bool canDelete;

  @override
  State<ItemEditor> createState() => _ItemEditorState();
}

class _ItemEditorState extends State<ItemEditor> {
  late final _bullets = TextEditingController(
      text: widget.bulletsField == null ? '' : ((widget.item[widget.bulletsField] as List?) ?? const []).join('\n'));
  String? _problem;

  static final _date = RegExp(r'^(19|20)\d{2}(-(0[1-9]|1[0-2]))?$');

  @override
  void dispose() {
    _bullets.dispose();
    super.dispose();
  }

  void _save() {
    for (final key in ['start', 'end', 'date']) {
      final v = str(widget.item[key]);
      if (v.isNotEmpty && !_date.hasMatch(v)) {
        setState(() => _problem = 'Dates look like 2023-01 or 2023');
        return;
      }
    }
    final start = str(widget.item['start']), end = str(widget.item['end']);
    if (start.isNotEmpty && end.isNotEmpty && end.compareTo(start) < 0) {
      setState(() => _problem = 'The end date is before the start');
      return;
    }
    if (widget.bulletsField != null) {
      widget.item[widget.bulletsField!] = [for (final l in _bullets.text.split('\n')) if (l.trim().isNotEmpty) l.trim().replaceFirst(RegExp(r'^[-•*]\s*'), '')];
    }
    Navigator.of(context).pop('save');
  }

  @override
  Widget build(BuildContext context) {
    final end = widget.endField;
    final stillHere = end != null && widget.item[end.$3] == true;
    return Scaffold(
      appBar: AppBar(title: Text(widget.title), actions: [
        if (widget.canDelete)
          IconButton(
            tooltip: 'Delete',
            icon: const Icon(Icons.delete_outline),
            onPressed: () => Navigator.of(context).pop('delete'),
          ),
        TextButton(onPressed: _save, child: const Text('Save')),
      ]),
      body: ListView(padding: const EdgeInsets.all(16), children: [
        for (final (key, label) in widget.fields) MapField(widget.item, key, label, onChanged: () {}),
        if (end != null) ...[
          CheckboxListTile(
            contentPadding: EdgeInsets.zero,
            value: stillHere,
            title: Text(end.$4),
            onChanged: (v) => setState(() {
              widget.item[end.$3] = v ?? false;
              if (v ?? false) widget.item[end.$1] = null;
            }),
          ),
          if (!stillHere) MapField(widget.item, end.$1, end.$2, onChanged: () {}),
        ],
        if (widget.bulletsField != null)
          TextField(
            controller: _bullets,
            minLines: 4,
            maxLines: 12,
            decoration: const InputDecoration(labelText: 'What you did (one point per line)', border: OutlineInputBorder(), alignLabelWithHint: true),
          ),
        if (_problem != null)
          Padding(padding: const EdgeInsets.only(top: 12), child: Text(_problem!, style: TextStyle(color: Theme.of(context).colorScheme.error))),
      ]),
    );
  }
}

class SkillsSection extends ConsumerWidget {
  const SkillsSection({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final skills = ctl.profile!['skills'] as Json;
    return SectionCard(title: 'Skills', subtitle: '${skillCount(ctl.profile!)} skills', children: [
      for (final category in skills.keys.toList())
        Padding(
          padding: const EdgeInsets.only(bottom: 14),
          child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
            GestureDetector(
              onLongPress: () => _manageCategory(context, ctl, category),
              child: Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Text(category, style: Theme.of(context).textTheme.labelLarge),
              ),
            ),
            ChipsEditor(key: ValueKey('skills-$category'), values: skills[category] as List, onChanged: ctl.changed, hint: 'Add a skill'),
          ]),
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(icon: const Icon(Icons.add), label: const Text('Add category'), onPressed: () => _addCategory(context, ctl)),
      ),
    ]);
  }

  Future<String?> _ask(BuildContext context, String title, [String initial = '']) {
    final text = TextEditingController(text: initial);
    return showDialog<String>(
      context: context,
      builder: (dialog) => AlertDialog(
        title: Text(title),
        content: TextField(controller: text, autofocus: true, textCapitalization: TextCapitalization.words),
        actions: [
          TextButton(onPressed: () => Navigator.pop(dialog), child: const Text('Cancel')),
          FilledButton(onPressed: () => Navigator.pop(dialog, text.text.trim()), child: const Text('OK')),
        ],
      ),
    );
  }

  Future<void> _addCategory(BuildContext context, ProfileController ctl) async {
    final name = await _ask(context, 'New category');
    final skills = ctl.profile!['skills'] as Json;
    if (name == null || name.isEmpty || skills.keys.any((k) => k.toLowerCase() == name.toLowerCase())) return;
    skills[name] = <String>[];
    ctl.changed();
  }

  /// Long-press a category: rename it, or delete it when it's empty.
  Future<void> _manageCategory(BuildContext context, ProfileController ctl, String category) async {
    final skills = ctl.profile!['skills'] as Json;
    final empty = (skills[category] as List).isEmpty;
    final name = await _ask(context, empty ? 'Rename (or clear the name to delete)' : 'Rename category', category);
    if (name == null) return;
    if (name.isEmpty) {
      if (empty) skills.remove(category);
    } else if (name != category && !skills.keys.any((k) => k.toLowerCase() == name.toLowerCase())) {
      final renamed = {for (final e in skills.entries) (e.key == category ? name : e.key): e.value};
      skills
        ..clear()
        ..addAll(renamed);
    }
    ctl.changed();
  }
}

/// Work authorization (spec §6.1 step 3): per country, plus clearance, government ties and the
/// companies worked for ("worked here before?" is answered per application from that list).
class AuthEditor extends ConsumerWidget {
  const AuthEditor({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final p = ctl.profile!;
    final auth = p['work_auth'] as Json;
    final byCountry = auth['by_country'] as Json;
    final geo = ref.watch(geoProvider).valueOrNull;
    final home = str((p['address'] as Map)['country']);
    if (home.isNotEmpty && geo != null && !byCountry.containsKey(home)) {
      byCountry[home] = {'name': geo.byCode(home)?.name ?? home};
    }
    final worked = auth['worked_at'] as List;
    if (worked.isEmpty && !((p['setup'] as Map)['completed'] as List).contains('auth')) {
      for (final role in (p['experience'] as List).cast<Map>()) {
        final company = str(role['company']);
        if (company.isNotEmpty && !worked.any((c) => c.toString().toLowerCase() == company.toLowerCase())) worked.add(company);
      }
    }

    void set(Json map, String key, bool v) {
      map[key] = v;
      ctl.changed();
    }

    return Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
      for (final code in byCountry.keys.toList())
        SectionCard(
          title: str((byCountry[code] as Map)['name']).isEmpty ? code : str((byCountry[code] as Map)['name']),
          trailing: code == home
              ? null
              : IconButton(
                  tooltip: 'Remove',
                  icon: const Icon(Icons.close),
                  onPressed: () {
                    byCountry.remove(code);
                    ctl.changed();
                  }),
          children: [
            YesNoRow(
              label: 'Are you authorized to work in ${str((byCountry[code] as Map)['name']).isEmpty ? code : (byCountry[code] as Map)['name']}?',
              value: (byCountry[code] as Map)['authorized'] as bool?,
              onChanged: (v) => set(byCountry[code] as Json, 'authorized', v),
            ),
            YesNoRow(
              label: 'Will you ever need visa sponsorship there, now or later?',
              value: (byCountry[code] as Map)['sponsorship'] as bool?,
              onChanged: (v) => set(byCountry[code] as Json, 'sponsorship', v),
            ),
            MapField(byCountry[code] as Json, 'visa', 'Visa or permit type (optional)', onChanged: ctl.changed),
          ],
        ),
      Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(
          icon: const Icon(Icons.add),
          label: const Text('Another country'),
          onPressed: geo == null
              ? null
              : () async {
                  final picked = await pickCountry(context, geo);
                  if (picked == null || byCountry.containsKey(picked.code)) return;
                  byCountry[picked.code] = {'name': picked.name};
                  ctl.changed();
                },
        ),
      ),
      SectionCard(title: 'Everywhere', children: [
        YesNoRow(label: 'Do you hold a security clearance?', value: auth['clearance'] as bool?, onChanged: (v) => set(auth, 'clearance', v)),
        YesNoRow(
            label: 'Do you have ties to a government or public office?',
            value: auth['government_ties'] as bool?,
            onChanged: (v) => set(auth, 'government_ties', v)),
      ]),
      SectionCard(
        title: 'Companies you\'ve worked for',
        subtitle: 'Used for "Have you worked for us before?" on each application.',
        children: [ChipsEditor(values: worked, onChanged: ctl.changed, hint: 'Add a company')],
      ),
    ]);
  }
}

class PrefsEditor extends ConsumerWidget {
  const PrefsEditor({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final prefs = ctl.profile!['preferences'] as Json;

    Widget box(String key, String title, String detail) => CheckboxListTile(
          contentPadding: EdgeInsets.zero,
          value: prefs[key] == true,
          title: Text(title),
          subtitle: Text(detail),
          onChanged: (v) {
            prefs[key] = v ?? false;
            markStep(ctl.profile!, 'prefs');
            ctl.changed();
          },
        );

    return SectionCard(title: 'Work preferences', children: [
      box('onsite_ok', 'Open to in-person', 'Office or onsite roles'),
      box('relocate', 'Willing to relocate', 'For the right role'),
      box('start_now', 'Can start right away', 'No notice period'),
      if (prefs['start_now'] != true) ...[
        Padding(
          padding: const EdgeInsets.only(bottom: 10),
          child: TextFormField(
            initialValue: prefs['notice_days'] == null ? '' : '${prefs['notice_days']}',
            keyboardType: TextInputType.number,
            decoration: const InputDecoration(labelText: 'Notice period in days', border: OutlineInputBorder(), isDense: true),
            onChanged: (v) {
              prefs['notice_days'] = int.tryParse(v.trim());
              ctl.changed();
            },
          ),
        ),
        MapField(prefs, 'earliest_start', 'Earliest start date (YYYY-MM-DD, optional)', onChanged: ctl.changed),
      ],
      box('transport', 'Have reliable transportation', 'To get to work'),
      box('accommodations', 'Need workplace accommodations', 'Only used when an employer asks'),
    ]);
  }
}

/// Expected salary (preferences.salary) or the floor for matches (targets.salary_floor).
class SalaryFields extends StatelessWidget {
  const SalaryFields({super.key, required this.salary, required this.onChanged, this.label = 'Expected salary'});

  final Json salary;
  final VoidCallback onChanged;
  final String label;

  @override
  Widget build(BuildContext context) {
    salary['currency'] = str(salary['currency']).isEmpty ? 'INR' : salary['currency'];
    salary['period'] = str(salary['period']).isEmpty ? 'year' : salary['period'];
    return Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Expanded(
        flex: 3,
        child: Padding(
          padding: const EdgeInsets.only(bottom: 10),
          child: TextFormField(
            initialValue: salary['amount'] == null ? '' : '${salary['amount']}',
            keyboardType: TextInputType.number,
            decoration: InputDecoration(labelText: label, border: const OutlineInputBorder(), isDense: true),
            onChanged: (v) {
              salary['amount'] = num.tryParse(v.replaceAll(RegExp(r'[,\s]'), ''));
              onChanged();
            },
          ),
        ),
      ),
      const SizedBox(width: 8),
      Expanded(
        flex: 2,
        child: TextFormField(
          initialValue: salary['currency'] as String,
          textCapitalization: TextCapitalization.characters,
          decoration: const InputDecoration(labelText: 'Currency', border: OutlineInputBorder(), isDense: true),
          onChanged: (v) {
            salary['currency'] = v.trim().toUpperCase();
            onChanged();
          },
        ),
      ),
      const SizedBox(width: 8),
      DropdownButton<String>(
        value: salary['period'] as String,
        items: const [
          DropdownMenuItem(value: 'year', child: Text('/ year')),
          DropdownMenuItem(value: 'month', child: Text('/ month')),
          DropdownMenuItem(value: 'hour', child: Text('/ hour')),
        ],
        onChanged: (v) {
          salary['period'] = v;
          onChanged();
        },
      ),
    ]);
  }
}

/// Voluntary self-identification (spec §4.2 background): "Prefer not to say" unless the user
/// chooses to answer. Never inferred from anything.
class BackgroundEditor extends ConsumerWidget {
  const BackgroundEditor({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final ctl = ref.watch(profileControllerProvider);
    final b = ctl.profile!['background'] as Json;
    final items = const [('gender', 'Gender'), ('ethnicity', 'Race / ethnicity'), ('veteran', 'Veteran status'), ('disability', 'Disability')];
    for (final (key, _) in items) {
      b[key] ??= 'decline';
    }
    return SectionCard(
      title: 'Background (voluntary)',
      subtitle: 'Only used when a form asks. "Prefer not to say" is picked unless you answer.',
      children: [
        for (final (key, label) in items) ...[
          SwitchListTile(
            contentPadding: EdgeInsets.zero,
            title: Text(label),
            subtitle: Text(b[key] == 'decline' ? 'Prefer not to say' : 'Answer: ${str(b[key]).isEmpty ? '(type below)' : b[key]}'),
            value: b[key] != 'decline',
            onChanged: (answer) {
              b[key] = answer ? '' : 'decline';
              ctl.changed();
            },
          ),
          if (b[key] != 'decline') MapField(b, key, label, key: ValueKey('bg-$key'), onChanged: ctl.changed),
        ],
        MapField(b, 'pronouns', 'Pronouns (optional)', onChanged: ctl.changed),
        MapField(b, 'additional', 'Anything else employers should know (optional)', lines: 3, onChanged: ctl.changed),
      ],
    );
  }
}
