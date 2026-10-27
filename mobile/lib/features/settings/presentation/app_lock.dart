import 'package:flutter/material.dart';
import 'package:local_auth/local_auth.dart';

final _localAuth = LocalAuthentication();

/// Fingerprint/face with the device PIN/pattern as fallback. False on cancel
/// or any platform error — callers treat that as "still locked".
Future<bool> authenticateUser(String reason) async {
  try {
    return await _localAuth.authenticate(
      localizedReason: reason,
      persistAcrossBackgrounding: true,
    );
  } catch (_) {
    return false;
  }
}

/// True if the device has a screen lock or biometrics to verify against.
Future<bool> canAuthenticate() async {
  try {
    return await _localAuth.isDeviceSupported();
  } catch (_) {
    return false;
  }
}

/// Opaque full-screen cover shown above the navigator while locked.
class AppLockScreen extends StatefulWidget {
  final VoidCallback onUnlocked;

  const AppLockScreen({super.key, required this.onUnlocked});

  @override
  State<AppLockScreen> createState() => _AppLockScreenState();
}

class _AppLockScreenState extends State<AppLockScreen> {
  bool _prompting = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _unlock());
  }

  Future<void> _unlock() async {
    if (_prompting) return;
    setState(() => _prompting = true);
    final ok = await authenticateUser('Unlock JARVIS');
    if (!mounted) return;
    setState(() => _prompting = false);
    if (ok) widget.onUnlocked();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.lock_outline, size: 64, color: theme.colorScheme.primary),
              const SizedBox(height: 16),
              Text('JARVIS is locked', style: theme.textTheme.titleLarge),
              const SizedBox(height: 24),
              FilledButton.icon(
                onPressed: _prompting ? null : _unlock,
                icon: const Icon(Icons.fingerprint),
                label: const Text('Unlock'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
