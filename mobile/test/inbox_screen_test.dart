import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/inbox/data/inbox_repository.dart';
import 'package:jarvis_mobile/features/inbox/presentation/inbox_screen.dart';
import 'package:mockito/mockito.dart';

class _FakeInbox extends Mock implements InboxRepository {
  final decisions = <String, bool>{};
  List<Proposal> items = const [
    Proposal(id: 'p1', title: 'Remember: likes tea', detail: '- likes tea', source: 'memory', status: 'pending'),
    Proposal(id: 'p2', title: 'Remember: works at Acme', detail: '- works at Acme', source: 'memory', status: 'pending'),
  ];

  @override
  Future<List<Proposal>> list() async => items;

  @override
  Future<int> count() async => items.length;

  @override
  Future<Proposal> decide(String id, bool approve) async {
    decisions[id] = approve;
    items = items.where((p) => p.id != id).toList();
    return Proposal(id: id, title: '', detail: '', source: '', status: approve ? 'done' : 'rejected');
  }
}

Widget _app(_FakeInbox repo) => ProviderScope(
      overrides: [inboxRepositoryProvider.overrideWithValue(repo)],
      child: const MaterialApp(home: InboxScreen()),
    );

void main() {
  testWidgets('shows each proposal and sends the decision', (tester) async {
    final repo = _FakeInbox();
    await tester.pumpWidget(_app(repo));
    await tester.pumpAndSettle();

    expect(find.text('Remember: likes tea'), findsOneWidget);
    expect(find.text('Remember: works at Acme'), findsOneWidget);

    await tester.tap(find.text('Approve').first);
    await tester.pumpAndSettle();

    expect(repo.decisions, {'p1': true});
    expect(find.text('Saved'), findsOneWidget); // the snackbar
    expect(find.text('Remember: likes tea'), findsNothing); // refreshed list
  });

  testWidgets('an empty inbox says so', (tester) async {
    final repo = _FakeInbox()..items = const [];
    await tester.pumpWidget(_app(repo));
    await tester.pumpAndSettle();
    expect(find.text('Nothing is waiting for you.'), findsOneWidget);
  });

  test('outcome wording covers every final status', () {
    String of(String s, [String r = '']) => Proposal(id: 'x', title: '', detail: '', source: '', status: s, result: r).outcome;
    expect(of('done'), 'Saved');
    expect(of('stale'), contains('changed'));
    expect(of('failed', 'no such file'), 'Failed: no such file');
  });
}
