/// The career profile (`Profile/profile.json`, 14-Career-Copilot.md §4.2), kept as the JSON map it
/// is on disk: unknown keys survive every save, so a newer app or the PC can add fields freely.
library;

import 'dart:math';

const profileSchema = 1;

const defaultSkillCategories = ['Languages', 'Frontend & Mobile', 'Backend & Data', 'AI / ML', 'Infrastructure & Tooling'];

/// The setup steps, in order; each id goes into `setup.completed` when its Next is pressed.
const setupSteps = ['resume', 'details', 'auth', 'prefs', 'password'];

typedef Json = Map<String, dynamic>;

Json _map(Json parent, String key) {
  final value = parent[key];
  if (value is Map) return parent[key] = Map<String, dynamic>.from(value);
  return parent[key] = <String, dynamic>{};
}

List<dynamic> _list(Json parent, String key) {
  final value = parent[key];
  if (value is List) return parent[key] = List<dynamic>.from(value);
  return parent[key] = <dynamic>[];
}

/// Fills in every section the screens expect, without touching values already there.
Json normalizeProfile(Json? raw) {
  final p = Map<String, dynamic>.from(raw ?? const {});
  p['schema'] ??= profileSchema;
  final setup = _map(p, 'setup');
  _list(setup, 'completed');
  for (final key in ['contact', 'address', 'links', 'background', 'targets']) {
    _map(p, key);
  }
  final auth = _map(p, 'work_auth');
  _map(auth, 'by_country');
  _list(auth, 'worked_at');
  final prefs = _map(p, 'preferences');
  _map(prefs, 'salary');
  for (final key in ['experience', 'education', 'projects', 'certificates', 'languages', 'resumes']) {
    _list(p, key);
  }
  final skills = _map(p, 'skills');
  for (final c in defaultSkillCategories) {
    skills[c] = List<dynamic>.from((skills[c] as List?) ?? const []);
  }
  for (final c in skills.keys.toList()) {
    skills[c] = List<dynamic>.from((skills[c] as List?) ?? const []);
  }
  final targets = p['targets'] as Json;
  for (final key in ['roles', 'locations', 'remote', 'avoid_companies']) {
    _list(targets, key);
  }
  _map(targets, 'salary_floor');
  for (final key in ['experience', 'education', 'projects', 'certificates']) {
    p[key] = [for (final e in p[key] as List) if (e is Map) Map<String, dynamic>.from(e)];
  }
  return p;
}

String str(Object? v) => v is String ? v.trim() : '';
bool filled(Object? v) => str(v).isNotEmpty;

bool setupFinished(Json p) => filled((p['setup'] as Map?)?['finished_at']);

void markStep(Json p, String step) {
  final done = (p['setup'] as Json)['completed'] as List;
  if (!done.contains(step)) done.add(step);
}

String nextId(List items, String prefix) {
  var n = 1;
  final ids = {for (final e in items) if (e is Map) e['id']};
  while (ids.contains('$prefix$n')) {
    n++;
  }
  return '$prefix$n';
}

/// "4 roles · Most recent: Backend Engineer at Acme"
String experienceHeadline(Json p) {
  final roles = p['experience'] as List;
  if (roles.isEmpty) return 'No roles yet';
  final top = roles.first as Map;
  final count = roles.length == 1 ? '1 role' : '${roles.length} roles';
  return '$count · Most recent: ${str(top['title'])} at ${str(top['company'])}';
}

int skillCount(Json p) => (p['skills'] as Map).values.fold(0, (n, v) => n + (v as List).length);

/// What the wizard says after reading a resume.
String foundLine(Map counts) {
  String n(int v, String one, String many) => '$v ${v == 1 ? one : many}';
  return 'Found ${n(counts['roles'] as int? ?? 0, 'role', 'roles')}, '
      '${n(counts['schools'] as int? ?? 0, 'school', 'schools')} and '
      '${n(counts['skills'] as int? ?? 0, 'skill', 'skills')}';
}

// ---- Merging a resume extraction --------------------------------------------------------------

String _fold(Object? v) => str(v).toLowerCase().replaceAll(RegExp(r'\s+'), ' ');

/// The calling code at the start of [phone] among [dials] ("+91 98765" → "+91"), longest first.
(String, String) splitPhone(String phone, Iterable<String> dials) {
  final p = phone.trim();
  if (!p.startsWith('+')) return ('', p);
  final digits = '+${p.substring(1).replaceAll(RegExp(r'\D'), '')}';
  final match = (dials.where((d) => d.isNotEmpty && digits.startsWith(d)).toList()..sort((a, b) => b.length - a.length));
  if (match.isEmpty) return ('', p);
  final code = match.first;
  // Drop the code's digits from the printed number, keeping the user's spacing for the rest.
  var seen = 0;
  var cut = 0;
  for (var i = 0; i < p.length && seen < code.length; i++) {
    if (p[i] == '+' || RegExp(r'\d').hasMatch(p[i])) seen++;
    cut = i + 1;
  }
  return (code, p.substring(cut).trim().replaceFirst(RegExp(r'^[-\s.)]+'), ''));
}

/// Adds what [extracted] (the server's checked resume part) has and the profile lacks. Never
/// overwrites a value the user has, never removes anything: a re-upload only adds.
/// Returns how many items were added.
// ponytail: additive merge, not the per-item accept/skip diff of spec §4.3; build that when a
// re-upload with real changes (a job title fixed on the resume) needs it.
int mergeExtraction(Json p, Json extracted, {required Iterable<String> dials, required String? Function(String) countryCode,
    required String? Function(String country, String region) regionCode}) {
  var added = 0;
  final contact = p['contact'] as Json;
  final got = Map<String, dynamic>.from((extracted['contact'] as Map?) ?? const {});
  for (final key in ['first_name', 'middle_name', 'last_name', 'email']) {
    if (!filled(contact[key]) && filled(got[key])) contact[key] = str(got[key]);
  }
  if (!filled(contact['phone']) && filled(got['phone'])) {
    final (code, number) = splitPhone(str(got['phone']), dials);
    contact['phone'] = number;
    if (code.isNotEmpty && !filled(contact['phone_country'])) contact['phone_country'] = code;
  }
  final address = p['address'] as Json;
  final where = Map<String, dynamic>.from((extracted['address'] as Map?) ?? const {});
  for (final key in ['line1', 'city', 'postal']) {
    if (!filled(address[key]) && filled(where[key])) address[key] = str(where[key]);
  }
  if (!filled(address['country']) && filled(where['country_name'])) {
    final code = countryCode(str(where['country_name']));
    if (code != null) {
      address['country'] = code;
      address['country_name'] = str(where['country_name']);
    }
  }
  if (!filled(address['region']) && filled(address['country']) && filled(where['region_name'])) {
    final code = regionCode(str(address['country']), str(where['region_name']));
    if (code != null) {
      address['region'] = code;
      address['region_name'] = str(where['region_name']);
    }
  }
  final links = p['links'] as Json;
  (extracted['links'] as Map? ?? const {}).forEach((k, v) {
    if (!filled(links[k]) && filled(v)) links[k] = str(v);
  });
  if (!filled(p['summary']) && filled(extracted['summary'])) p['summary'] = str(extracted['summary']);

  void addItems(String key, String prefix, String Function(Map) identity) {
    final mine = p[key] as List;
    final have = {for (final e in mine) identity(e as Map)};
    for (final e in (extracted[key] as List?) ?? const []) {
      if (e is! Map || have.contains(identity(e))) continue;
      mine.add(Map<String, dynamic>.from(e)..['id'] = nextId(mine, prefix));
      have.add(identity(e));
      added++;
    }
  }

  addItems('experience', 'e', (e) => '${_fold(e['company'])}|${_fold(e['title'])}|${e['start']}');
  addItems('education', 'd', (e) => '${_fold(e['school'])}|${_fold(e['degree'])}');
  addItems('projects', 'p', (e) => _fold(e['name']));
  addItems('certificates', 'c', (e) => _fold(e['name']));
  final experience = p['experience'] as List;
  experience.sort((a, b) {
    String key(Map e) => '${e['current'] == true ? 1 : 0}${e['end'] ?? e['start'] ?? ''}';
    return key(b as Map).compareTo(key(a as Map));
  });

  final skills = p['skills'] as Json;
  final taken = {for (final v in skills.values) for (final s in v as List) _fold(s)};
  ((extracted['skills'] as Map?) ?? const {}).forEach((category, items) {
    for (final s in (items as List? ?? const [])) {
      if (!filled(s) || taken.contains(_fold(s))) continue;
      skills[category as String] ??= <dynamic>[];
      (skills[category] as List).add(str(s));
      taken.add(_fold(s));
      added++;
    }
  });
  return added;
}

// ---- Completeness (spec §4.5) -----------------------------------------------------------------

class Missing {
  const Missing(this.label, this.section, this.weight);

  final String label;

  /// Which editor fixes it: 'contact', 'address', 'experience', 'education', 'skills', 'summary',
  /// 'links', 'auth', 'prefs', 'salary', 'targets', 'background'.
  final String section;
  final int weight;
}

/// (percent, what's missing), from the weights in spec §4.5. [hasRegions]: whether the
/// profile's country has states/provinces to pick.
(int, List<Missing>) completeness(Json p, {bool hasRegions = false}) {
  final c = p['contact'] as Map;
  final a = p['address'] as Map;
  final done = (p['setup'] as Map)['completed'] as List;
  final home = str(a['country']);
  final homeAuth = ((p['work_auth'] as Map)['by_country'] as Map)[home];
  final salary = (p['preferences'] as Map)['salary'] as Map;
  final targets = p['targets'] as Map;
  final background = p['background'] as Map;
  final checks = <(bool, Missing)>[
    (filled(c['first_name']) && filled(c['email']) && filled(c['phone']), const Missing('Name, email and phone', 'contact', 15)),
    (filled(a['line1']) && filled(a['city']) && filled(a['postal']) && home.isNotEmpty && (!hasRegions || filled(a['region'])),
        const Missing('Where you live', 'address', 10)),
    ((p['experience'] as List).any((e) => filled(e['title']) && filled(e['company']) && filled(e['start'])),
        const Missing('Work experience', 'experience', 15)),
    ((p['education'] as List).any((e) => filled(e['degree']) && filled(e['school'])), const Missing('Education', 'education', 10)),
    (skillCount(p) >= 5, const Missing('At least 5 skills', 'skills', 10)),
    (filled(p['summary']), const Missing('Summary — missing on your resume', 'summary', 5)),
    (['linkedin', 'github', 'website'].any((k) => filled((p['links'] as Map)[k])), const Missing('Links: LinkedIn, GitHub or website', 'links', 5)),
    (homeAuth is Map && homeAuth['authorized'] is bool && homeAuth['sponsorship'] is bool,
        const Missing('Work authorization for your country', 'auth', 10)),
    (done.contains('prefs'), const Missing('Work preferences', 'prefs', 5)),
    (salary['amount'] is num && filled(salary['currency']), const Missing('Expected salary', 'salary', 5)),
    ((targets['roles'] as List).isNotEmpty && (targets['locations'] as List).isNotEmpty,
        const Missing('What you\'re looking for: roles and locations', 'targets', 5)),
    (background.isNotEmpty || done.contains('background'), const Missing('Background and voluntary info', 'background', 5)),
  ];
  final missing = [for (final (ok, m) in checks) if (!ok) m];
  final score = 100 - missing.fold<int>(0, (n, m) => n + m.weight);
  return (max(0, score), missing);
}

// ---- Portal password (spec §8.2) --------------------------------------------------------------

const _upper = 'ABCDEFGHJKLMNPQRSTUVWXYZ';
const _lower = 'abcdefghijkmnopqrstuvwxyz';
const _digits = '23456789';
const _special = '!@#\$%^&*';

/// 20 characters with every class job portals ask for, from a secure random source.
String generatePortalPassword([Random? random]) {
  final r = random ?? Random.secure();
  String pick(String set) => set[r.nextInt(set.length)];
  final all = '$_upper$_lower$_digits$_special';
  final chars = [pick(_upper), pick(_lower), pick(_digits), pick(_special), for (var i = 0; i < 16; i++) pick(all)]..shuffle(r);
  return chars.join();
}

/// Null when it's fine, else what's missing.
String? portalPasswordProblem(String password) {
  if (password.length < 12) return 'Use at least 12 characters';
  if (!RegExp('[A-Z]').hasMatch(password)) return 'Add a capital letter';
  if (!RegExp('[a-z]').hasMatch(password)) return 'Add a small letter';
  if (!RegExp(r'\d').hasMatch(password)) return 'Add a number';
  if (!RegExp(r'[^A-Za-z0-9]').hasMatch(password)) return 'Add a symbol such as ! or #';
  return null;
}
