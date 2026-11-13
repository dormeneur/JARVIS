import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/autofill/autofill_repository.dart';
import 'package:jarvis_mobile/features/autofill/autofill_sheet.dart';

/// The PC's answers, without a PC.
class _Vault implements AutofillRepository {
  _Vault(this.brain);

  final Map<String, String?> brain;
  final asked = <String, String>{};
  String? written;

  @override
  Stream<MapEntry<String, String?>> answers(String title, Map<String, String> questions) async* {
    asked.addAll(questions);
    for (final id in questions.keys) {
      yield MapEntry(id, brain[questions[id]]);
    }
  }

  /// What the sheet queued on the phone, and whether the PC took it.
  final queued = <String>[];
  bool pcUp = true;
  bool flushed = false;

  @override
  Future<void> queueLearned(String title, List<String> labels, List<String?> values) async => queued.add('$title|${labels.join(',')}|${values.join(',')}');

  @override
  Future<void> flushPending() async {
    if (!pcUp) throw Exception('PC down');
    flushed = true;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  Future<List<MethodCall>> run(WidgetTester tester, Map<String, Object?> request, _Vault vault) async {
    final calls = <MethodCall>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(AutofillSheet.channel, (call) async {
      calls.add(call);
      return switch (call.method) {
        'request' => request,
        'merge' => '{"merged":true}',
        _ => null,
      };
    });
    addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(AutofillSheet.channel, null));
    await tester.pumpWidget(ProviderScope(overrides: [autofillRepositoryProvider.overrideWithValue(vault)], child: const AutofillSheetApp()));
    for (var i = 0; i < 20 && !calls.any((call) => call.method == 'finish'); i++) {
      await tester.pump(const Duration(milliseconds: 200));
    }
    return calls;
  }

  testWidgets('Fill: memory goes in at once, only the rest is asked of the vault', (tester) async {
    final vault = _Vault({'Notice period': '4 weeks', 'Favourite colour': null});
    final calls = await run(tester, {
      'mode': 'fill',
      'title': 'example.test',
      'labels': ['Given Name', 'Notice period', 'Favourite colour'],
      'known': ['Asha', '', ''],
    }, vault);

    expect(vault.asked.values, ['Notice period', 'Favourite colour']);
    expect(calls.last.method, 'finish');
    expect(calls.last.arguments['values'], ['Asha', '4 weeks', null]);
  });

  testWidgets('Text: the selected words are the question; the answer picked replaces them', (tester) async {
    final vault = _Vault({'passport number': 'Z1234567'});
    final calls = <MethodCall>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(AutofillSheet.channel, (call) async {
      calls.add(call);
      return call.method == 'request'
          ? {'mode': 'text', 'title': '', 'labels': ['passport number'], 'known': [''], 'choices': ['Y7654321'], 'readOnly': false}
          : null;
    });
    addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(AutofillSheet.channel, null));
    await tester.pumpWidget(ProviderScope(overrides: [autofillRepositoryProvider.overrideWithValue(vault)], child: const AutofillSheetApp()));
    await tester.pump(const Duration(milliseconds: 200));

    expect(find.text('Y7654321'), findsOneWidget); // from memory
    expect(find.text('Z1234567'), findsOneWidget); // from the vault
    expect(calls.where((call) => call.method == 'finish'), isEmpty); // nothing goes in until one is picked
    await tester.tap(find.text('Z1234567'));
    await tester.pump();

    expect(calls.last.method, 'finish');
    expect(calls.last.arguments['values'], ['Z1234567']);
  });

  testWidgets('Save: what was sent is queued on the phone, then pushed to the PC', (tester) async {
    final vault = _Vault({});
    await run(tester, {'mode': 'save', 'title': 'example.test', 'labels': ['Given Name'], 'values': ['Asha']}, vault);

    expect(vault.queued, ['example.test|Given Name|Asha']);
    expect(vault.flushed, isTrue);
    expect(find.textContaining('Remembered'), findsOneWidget);
  });

  testWidgets('Save: with the PC down it is still kept on the phone, to sync later', (tester) async {
    final vault = _Vault({})..pcUp = false;
    await run(tester, {'mode': 'save', 'title': 'example.test', 'labels': ['Given Name'], 'values': ['Asha']}, vault);

    expect(vault.queued, hasLength(1));
    expect(find.textContaining('on your phone'), findsOneWidget);
  });
}
