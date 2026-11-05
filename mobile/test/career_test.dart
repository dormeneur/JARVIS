import 'dart:math';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/career/data/career_repository.dart';
import 'package:jarvis_mobile/features/career/domain/career_profile.dart';
import 'package:jarvis_mobile/features/career/presentation/career_screen.dart';
import 'package:jarvis_mobile/features/career/presentation/career_widgets.dart';
import 'package:mockito/mockito.dart';

final _geo = Geo([
  const Country('IN', 'India', '+91', [('IN-KA', 'Karnataka'), ('IN-MH', 'Maharashtra')]),
  const Country('TH', 'Thailand', '+66', [('TH-10', 'Bangkok')]),
  const Country('SG', 'Singapore', '+65', []),
  const Country('US', 'United States', '+1', [('US-CA', 'California')]),
]);

class _FakeRepo extends Mock implements CareerRepository {
  _FakeRepo([this.stored]);

  Map<String, dynamic>? stored;
  final writes = <Map<String, dynamic>>[];

  @override
  Future<Map<String, dynamic>?> readProfile() async => stored;

  @override
  Future<void> writeProfile(Map<String, dynamic> profile) async => writes.add(Map.of(profile));

  @override
  Future<List<String>> listFiles(String folder) async => const [];

  @override
  Future<String?> portalPassword() async => null;
}

Widget _app(_FakeRepo repo) => ProviderScope(
      overrides: [
        careerRepositoryProvider.overrideWithValue(repo),
        geoProvider.overrideWith((ref) async => _geo),
      ],
      child: const MaterialApp(home: CareerScreen()),
    );

Map<String, dynamic> _extracted() => {
      'contact': {'first_name': 'Asha', 'middle_name': '', 'last_name': 'Rao', 'email': 'asha@example.com', 'phone': '+91 98765 43210'},
      'address': {'line1': '12 MG Road', 'city': 'Bengaluru', 'postal': '560001', 'region_name': 'Karnataka', 'country_name': 'India'},
      'links': {'github': 'github.com/asha', 'linkedin': '', 'website': ''},
      'summary': '',
      'experience': [
        {'id': 'e1', 'title': 'Backend Engineer', 'company': 'Acme', 'start': '2023-01', 'end': null, 'current': true, 'bullets': []},
        {'id': 'e2', 'title': 'Intern', 'company': 'Beta', 'start': '2021-06', 'end': '2021-12', 'current': false, 'bullets': []},
      ],
      'education': [{'id': 'd1', 'degree': 'B.Tech', 'major': 'CS', 'school': 'IIT Delhi', 'start': '2017', 'end': '2021'}],
      'skills': {'Languages': ['Go', 'Python'], 'Tools': ['Docker']},
      'projects': [],
      'certificates': [],
    };

void main() {
  group('profile logic', () {
    test('normalize keeps unknown keys and fills every section', () {
      final p = normalizeProfile({'future_field': 1, 'skills': {'Mine': ['x']}});
      expect(p['future_field'], 1);
      expect(p['schema'], profileSchema);
      expect((p['skills'] as Map).keys, containsAll([...defaultSkillCategories, 'Mine']));
      expect((p['work_auth'] as Map)['by_country'], isA<Map>());
      expect(setupFinished(p), isFalse);
    });

    test('a resume fills blanks, splits the phone, maps country and region', () {
      final p = normalizeProfile(null);
      final added = mergeExtraction(p, _extracted(), dials: _geo.dials, countryCode: _geo.codeOf, regionCode: _geo.regionOf);
      final c = p['contact'] as Map, a = p['address'] as Map;
      expect((c['phone_country'], c['phone']), ('+91', '98765 43210'));
      expect((a['country'], a['region']), ('IN', 'IN-KA'));
      expect(experienceHeadline(p), '2 roles · Most recent: Backend Engineer at Acme');
      expect(added, 2 + 1 + 3);
      expect(((p['skills'] as Map)['Tools'] as List), ['Docker']);
    });

    test('a re-upload only adds: user edits and existing items stay', () {
      final p = normalizeProfile(null);
      mergeExtraction(p, _extracted(), dials: _geo.dials, countryCode: _geo.codeOf, regionCode: _geo.regionOf);
      (p['contact'] as Map)['first_name'] = 'Ashu';
      ((p['experience'] as List).first as Map)['title'] = 'Senior Backend Engineer';
      final again = _extracted();
      (again['experience'] as List).add({'title': 'Founder', 'company': 'Gamma', 'start': '2024-02', 'end': null, 'current': true});
      (again['skills'] as Map)['Languages'] = ['go', 'Rust'];
      final added = mergeExtraction(p, again, dials: _geo.dials, countryCode: _geo.codeOf, regionCode: _geo.regionOf);
      expect((p['contact'] as Map)['first_name'], 'Ashu');
      final titles = [for (final e in (p['experience'] as List).cast<Map>()) e['title']];
      expect(titles, contains('Senior Backend Engineer'));
      expect(titles, contains('Founder'));
      // "Backend Engineer at Acme" no longer matches the edited role, so it comes back; the user deletes it.
      expect(added, 1 + 1 + 1);
      expect(((p['skills'] as Map)['Languages'] as List), ['Go', 'Python', 'Rust']);
      expect({for (final e in (p['experience'] as List).cast<Map>()) e['id']}.length, (p['experience'] as List).length);
    });

    test('phone split picks the longest calling code and leaves local numbers alone', () {
      expect(splitPhone('+1 415 555 0100', ['+1', '+44']), ('+1', '415 555 0100'));
      expect(splitPhone('+66-81-234-5678', ['+6', '+66']), ('+66', '81-234-5678'));
      expect(splitPhone('98765 43210', ['+91']), ('', '98765 43210'));
    });

    test('completeness follows the weights', () {
      final p = normalizeProfile(null);
      expect(completeness(p).$1, 0);
      mergeExtraction(p, _extracted(), dials: _geo.dials, countryCode: _geo.codeOf, regionCode: _geo.regionOf);
      // contact 15 + address 10 + experience 15 + education 10 + links 5 = 55 (3 skills < 5).
      expect(completeness(p, hasRegions: true).$1, 55);
      ((p['skills'] as Map)['Languages'] as List).addAll(['C', 'Java']);
      ((p['work_auth'] as Map)['by_country'] as Map)['IN'] = {'authorized': true, 'sponsorship': false};
      markStep(p, 'prefs');
      (p['background'] as Map)['gender'] = 'decline';
      final (percent, missing) = completeness(p, hasRegions: true);
      expect(percent, 55 + 10 + 10 + 5 + 5);
      expect(missing.map((m) => m.section), ['summary', 'salary', 'targets']);
    });

    test('portal passwords meet the rules', () {
      for (var i = 0; i < 50; i++) {
        final pw = generatePortalPassword(Random(i));
        expect(pw.length, 20);
        expect(portalPasswordProblem(pw), isNull, reason: pw);
      }
      expect(portalPasswordProblem('short1!A'), isNotNull);
      expect(portalPasswordProblem('alllowercase123!'), 'Add a capital letter');
      expect(portalPasswordProblem('NoSymbolsHere123'), 'Add a symbol such as ! or #');
    });

    test('found line', () {
      expect(foundLine({'roles': 4, 'schools': 1, 'skills': 39}), 'Found 4 roles, 1 school and 39 skills');
    });
  });

  group('screens', () {
    testWidgets('first open shows the wizard; the details step needs a name and email', (tester) async {
      final repo = _FakeRepo();
      await tester.pumpWidget(_app(repo));
      await tester.pumpAndSettle();
      expect(find.text('Upload your resume'), findsOneWidget);
      expect(find.text('Choose resume (PDF or Word)'), findsOneWidget);

      await tester.tap(find.text('Skip'));
      await tester.pumpAndSettle();
      expect(find.text('Review your details'), findsOneWidget);
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();
      expect(find.text('Add your first and last name'), findsOneWidget);
      expect(find.text('Review your details'), findsOneWidget);

      await tester.enterText(find.widgetWithText(TextField, 'First name'), 'Asha');
      await tester.enterText(find.widgetWithText(TextField, 'Last name'), 'Rao');
      await tester.enterText(find.widgetWithText(TextField, 'Email'), 'asha@example.com');
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();
      expect(find.text('Work authorization'), findsOneWidget);
      await tester.pump(const Duration(seconds: 1)); // the debounced save
      expect(repo.writes.last['contact']['first_name'], 'Asha');
      expect(repo.writes.last['setup']['completed'], ['resume', 'details']);
    });

    testWidgets('a finished setup opens the profile with its completeness', (tester) async {
      final p = normalizeProfile({'setup': {'completed': setupSteps, 'finished_at': '2026-11-05'}});
      mergeExtraction(p, _extracted(), dials: _geo.dials, countryCode: _geo.codeOf, regionCode: _geo.regionOf);
      await tester.pumpWidget(_app(_FakeRepo(p)));
      await tester.pumpAndSettle();
      expect(find.text('Career profile'), findsOneWidget);
      expect(find.textContaining('% complete'), findsOneWidget);
      expect(find.text('2 roles · Most recent: Backend Engineer at Acme'), findsOneWidget);
      await tester.tap(find.textContaining('Fill in the rest'));
      await tester.pumpAndSettle();
      expect(find.text('Summary — missing on your resume'), findsOneWidget);
    });
  });

  group('editors', () {
    Widget host(Widget child) => MaterialApp(home: Scaffold(body: SingleChildScrollView(child: child)));

    testWidgets('chips: Enter adds, comma lists add several, duplicates refused, × deletes', (tester) async {
      final values = <dynamic>['Go'];
      var changes = 0;
      await tester.pumpWidget(host(ChipsEditor(values: values, onChanged: () => changes++)));
      await tester.enterText(find.byType(TextField), 'Python, Rust');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(values, ['Go', 'Python', 'Rust']);
      await tester.enterText(find.byType(TextField), 'go');
      await tester.testTextInput.receiveAction(TextInputAction.done);
      await tester.pumpAndSettle();
      expect(values, ['Go', 'Python', 'Rust']);
      expect(find.text('Already added'), findsOneWidget);
      await tester.tap(find.byTooltip('Delete').first);
      await tester.pumpAndSettle();
      expect(values, ['Python', 'Rust']);
      expect(changes, greaterThan(0));
    });

    testWidgets('picking a country offers its states; a country without them gets a text box', (tester) async {
      tester.view.physicalSize = const Size(800, 2400);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final p = normalizeProfile({'setup': {'completed': setupSteps, 'finished_at': 'x'}});
      final repo = _FakeRepo(p);
      await tester.pumpWidget(ProviderScope(
        overrides: [careerRepositoryProvider.overrideWithValue(repo), geoProvider.overrideWith((ref) async => _geo)],
        child: const MaterialApp(home: CareerScreen()),
      ));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Personal'));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('country-picker')));
      await tester.pumpAndSettle();
      await tester.enterText(find.widgetWithText(TextField, 'Search countries'), 'Ind');
      await tester.pumpAndSettle();
      await tester.tap(find.text('India'));
      await tester.pumpAndSettle();
      expect(find.text('State / Province'), findsOneWidget);
      await tester.pump(const Duration(seconds: 1)); // the debounced save
      expect(repo.writes.last['address']['country'], 'IN');
      expect(repo.writes.last['contact']['phone_country'], '+91');

      await tester.tap(find.byKey(const Key('country-picker')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Singapore'));
      await tester.pumpAndSettle();
      expect(find.text('State / Province (if any)'), findsOneWidget);
      await tester.pump(const Duration(seconds: 1));
      expect(repo.writes.last['address']['country'], 'SG');
      expect(repo.writes.last['address']['region'], '');
    });
  });
}
