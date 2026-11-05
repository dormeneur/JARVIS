import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';
import 'package:jarvis_mobile/features/auth/data/auth_repository.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/home/presentation/home_screen.dart';
import 'package:jarvis_mobile/features/inbox/data/inbox_repository.dart';
import 'package:jarvis_mobile/features/voice/voice_service.dart';

class _NoRepo implements AuthRepository {
  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}

class _NoStorage implements SecureStorage {
  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}

class _NoClient implements ApiClient {
  @override
  dynamic noSuchMethod(Invocation invocation) => null;
}

/// Signed in as [admin] or a guest device; nothing else is touched.
class _SignedIn extends AuthNotifier {
  _SignedIn({required bool admin}) : super(_NoRepo(), _NoStorage(), _NoClient()) {
    state = AuthState(status: AuthStatus.authenticated, deviceName: 'Pixel', isSecretsAuthorized: admin);
  }
}

Widget _home({required bool admin, List<ChatSession> recent = const []}) => ProviderScope(
      overrides: [
        authProvider.overrideWith((ref) => _SignedIn(admin: admin)),
        recentChatsProvider.overrideWith((ref) async => recent),
        inboxCountProvider.overrideWith((ref) async => 0),
      ],
      child: const MaterialApp(home: HomeScreen()),
    );

void main() {
  testWidgets('Home offers asking by voice or text, the main places, and recent chats', (tester) async {
    final recent = [
      ChatSession(id: 's1', title: 'Trip to Lisbon', createdAt: '2026-11-01T10:00:00Z', lastActiveAt: '2026-11-02T10:00:00Z', status: 'inactive'),
    ];
    tester.view.physicalSize = const Size(1080, 2400); // a phone, so the whole home is laid out
    tester.view.devicePixelRatio = 2.5;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(_home(admin: true, recent: recent));
    await tester.pumpAndSettle();

    expect(find.text('Ask JARVIS'), findsOneWidget);
    expect(find.text('Or type a question…'), findsOneWidget);
    for (final tile in ['Chat', 'Interview', 'Files', 'Jobs', 'Settings']) {
      expect(find.text(tile), findsOneWidget);
    }
    expect(find.text('On Pixel'), findsOneWidget);
    expect(find.text('Lately'), findsOneWidget);
    expect(find.text('Trip to Lisbon'), findsOneWidget);
  });

  testWidgets('Job search (it spends the Claude subscription) is only on admin devices', (tester) async {
    await tester.pumpWidget(_home(admin: false));
    await tester.pumpAndSettle();

    expect(find.text('Jobs'), findsNothing);
    expect(find.text('Lately'), findsNothing); // no chats yet: no empty section
  });

  test('Spoken answers drop Markdown, code and link addresses', () {
    expect(
      VoiceService.spokenText('**Done.** See [the plan](https://x.test/p) ```code``` and `notes.md`.\n\n# Next'),
      'Done. See the plan and notes.md. Next',
    );
  });
}
