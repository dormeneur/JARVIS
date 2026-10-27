import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/auth/presentation/setup_screen.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_screen.dart';
import 'package:jarvis_mobile/features/settings/presentation/app_lock.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_provider.dart';

/// Root widget — manages auth state and routes accordingly.
class JarvisApp extends ConsumerStatefulWidget {
  const JarvisApp({super.key});

  @override
  ConsumerState<JarvisApp> createState() => _JarvisAppState();
}

class _JarvisAppState extends ConsumerState<JarvisApp> with WidgetsBindingObserver {
  // Short trips out of the app (file picker, share sheet) shouldn't re-lock.
  static const _relockAfter = Duration(seconds: 60);

  late bool _locked;
  DateTime? _backgroundedAt;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _locked = ref.read(appLockProvider);
    // Wire the API client's 401 hook to the auth state machine so a token
    // that expires mid-session (not just at startup) bounces the user back
    // to the login screen instead of leaving a dead session behind a stale
    // "authenticated" UI.
    ref.read(apiClientProvider).onUnauthorized = () {
      ref.read(authProvider.notifier).handleUnauthorized();
    };
    // Initialize auth on startup: load JWT, validate via /auth/me
    Future.microtask(() {
      ref.read(authProvider.notifier).initialize();
    });
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.paused || state == AppLifecycleState.hidden) {
      _backgroundedAt ??= DateTime.now();
    } else if (state == AppLifecycleState.resumed) {
      final away = _backgroundedAt;
      _backgroundedAt = null;
      if (ref.read(appLockProvider) && away != null && DateTime.now().difference(away) > _relockAfter) {
        setState(() => _locked = true);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final authState = ref.watch(authProvider);
    final showLock = _locked &&
        ref.watch(appLockProvider) &&
        authState.status == AuthStatus.authenticated;

    return MaterialApp(
      title: 'JARVIS',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xFF4A90D9),
          brightness: Brightness.light,
        ),
        useMaterial3: true,
        fontFamily: 'Roboto',
      ),
      darkTheme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: const Color(0xFF4A90D9),
          brightness: Brightness.dark,
        ),
        useMaterial3: true,
        fontFamily: 'Roboto',
      ),
      themeMode: ThemeMode.system,
      home: _buildHome(authState),
      // Cover the navigator instead of replacing it, so locking never
      // disposes an open editor with unsaved text.
      builder: (context, child) => Stack(
        children: [
          ExcludeSemantics(excluding: showLock, child: child!),
          if (showLock) AppLockScreen(onUnlocked: () => setState(() => _locked = false)),
        ],
      ),
    );
  }

  Widget _buildHome(AuthState authState) {
    switch (authState.status) {
      case AuthStatus.loading:
        return const Scaffold(
          body: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                CircularProgressIndicator(),
                SizedBox(height: 16),
                Text('Connecting…'),
              ],
            ),
          ),
        );
      case AuthStatus.unauthenticated:
        return const SetupScreen();
      case AuthStatus.authenticated:
        return const ExplorerScreen();
    }
  }
}
