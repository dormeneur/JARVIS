import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/chat/presentation/chat_screen.dart';
import 'package:jarvis_mobile/features/chat/data/chat_repository.dart';
import 'package:mockito/mockito.dart';

class MockChatRepository extends Mock implements ChatRepository {
  @override
  Stream<String> askJarvis(String? query) {
    if (query == 'error') {
      return Stream.error('Forced error');
    }
    return Stream.fromIterable([
      'Hello ', 'World', '{"answer": "Hello World", "sources": []}'
    ]);
  }
}

void main() {
  testWidgets('ChatScreen sends query and displays streaming response', (tester) async {
    final mockRepo = MockChatRepository();
    
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          chatRepositoryProvider.overrideWithValue(mockRepo),
        ],
        child: const MaterialApp(
          home: ChatScreen(),
        ),
      ),
    );

    // Initial state
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
  });
}
