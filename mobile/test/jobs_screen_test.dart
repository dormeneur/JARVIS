import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/jobs/data/jobs_repository.dart';
import 'package:jarvis_mobile/features/jobs/presentation/jobs_screen.dart';
import 'package:mockito/mockito.dart';

class _FakeJobsRepo extends Mock implements JobsRepository {
  @override
  Future<Map<String, dynamic>> status() async => {
        'claude': {'ok': true},
        'workspace_ok': true,
        'running': 0,
        'queued': 0,
      };

  @override
  Future<List<Map<String, dynamic>>> board() async => [
        {'key': 'a', 'title': 'ML Engineer', 'company': 'Acme', 'stage': 'shortlist', 'score': 81, 'guard': 'clean'},
        {'key': 'b', 'title': 'Backend Dev', 'company': 'Beta', 'stage': 'ready', 'score': 70},
      ];

  @override
  Future<List<Map<String, dynamic>>> runs() async => [
        {'id': 'r1', 'kind': 'apply', 'status': 'awaiting_input',
         'envelope': {'question': 'Should I proceed with drafting?'}},
      ];
}

void main() {
  testWidgets('board groups jobs by stage and surfaces pending questions', (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [jobsRepositoryProvider.overrideWithValue(_FakeJobsRepo())],
      child: const MaterialApp(home: JobsScreen()),
    ));
    await tester.pumpAndSettle();

    expect(find.text('Should I proceed with drafting?'), findsOneWidget);
    expect(find.text('Ready to submit (1)'), findsOneWidget);
    expect(find.text('Shortlist (1)'), findsOneWidget);
    expect(find.text('ML Engineer'), findsOneWidget);
    expect(find.text('81'), findsOneWidget);
    expect(find.text('Find jobs'), findsOneWidget);
  });
}
