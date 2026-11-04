import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/app.dart';
import 'package:jarvis_mobile/features/autofill/autofill_sheet.dart';
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

/// The sheet Android opens over another app's form (AutofillSheetActivity): its own small app,
/// sharing the signed-in session with this one.
@pragma('vm:entry-point')
void autofillMain() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const ProviderScope(child: AutofillSheetApp()));
}
