import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/app.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_provider.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final appLockEnabled = await AppLockNotifier.loadInitial();
  runApp(ProviderScope(
    overrides: [
      appLockProvider.overrideWith((ref) => AppLockNotifier(appLockEnabled)),
    ],
    child: const JarvisApp(),
  ));
}
