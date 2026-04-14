import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/chat/presentation/chat_screen.dart';
import 'package:jarvis_mobile/features/chat/data/chat_repository.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:mockito/mockito.dart';
import 'package:drift/native.dart';

class MockChatRepository extends Mock implements ChatRepository {
  @override
  Stream<String> askJarvis(String query, {
    List<String>? attachments,
    List<Map<String, dynamic>>? chatHistory,
    String currentDirectory = '',
  }) {
    if (query == 'error') {
      return Stream.error('Forced error');
    }
    return Stream.fromIterable([
      'Hello ', 'World', '{"answer": "Hello World", "sources": []}'
    ]);
  }

  @override
  Future<bool> checkAiStatus() async => true;

  @override
  Future<String> triggerReindex() async => 'indexing_started';
}

void main() {
  testWidgets('ChatScreen sends query and displays streaming response', (tester) async {
    final mockRepo = MockChatRepository();
    final db = AppDatabase.forTesting(NativeDatabase.memory());

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          chatRepositoryProvider.overrideWithValue(mockRepo),
          appDatabaseProvider.overrideWithValue(db),
        ],
        child: const MaterialApp(
          home: ChatScreen(),
        ),
      ),
    );

    // Initial state — wait for status check
    await tester.pumpAndSettle();
    expect(find.text('JARVIS AI'), findsOneWidget);

    // Type query
    await tester.enterText(find.byType(TextField), 'Hello JARVIS');
    await tester.tap(find.byIcon(Icons.send));
    await tester.pump();

    // Verify user message appears
    expect(find.text('Hello JARVIS'), findsOneWidget);

    // Pump stream ticks
    await tester.pumpAndSettle();

    // Verify response
    expect(find.text('Hello World'), findsOneWidget);

    await db.close();
  });
}
