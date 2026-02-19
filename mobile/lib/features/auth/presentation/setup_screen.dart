import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/auth/presentation/register_screen.dart';

/// Screen where the user enters the JARVIS server URL.
///
/// For Tailscale access, use the Tailscale IP:
/// e.g., http://100.x.x.x:8000
class SetupScreen extends ConsumerStatefulWidget {
  const SetupScreen({super.key});

  @override
  ConsumerState<SetupScreen> createState() => _SetupScreenState();
}

class _SetupScreenState extends ConsumerState<SetupScreen> {
  final _urlController = TextEditingController(text: 'http://');
  bool _loading = false;
  String? _error;

  @override
  void dispose() {
    _urlController.dispose();
    super.dispose();
  }

  Future<void> _checkAndProceed() async {
    final url = _urlController.text.trim();
    if (url.isEmpty || !url.startsWith('http')) {
      setState(
        () => _error = 'Enter a valid URL (e.g., http://100.x.x.x:8000)',
      );
      return;
    }

    setState(() {
      _loading = true;
      _error = null;
    });

    final authRepo = ref.read(authRepositoryProvider);
    final isReachable = await authRepo.checkServerHealth(url);

    if (!mounted) return;

    if (!isReachable) {
      setState(() {
        _loading = false;
        _error = 'Cannot reach server at $url.\nCheck Tailscale connection.';
      });
      return;
    }

    setState(() => _loading = false);

    if (!mounted) return;
    Navigator.of(
      context,
    ).push(MaterialPageRoute(builder: (_) => RegisterScreen(serverUrl: url)));
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: SafeArea(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 32),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Icon(
                Icons.cloud_outlined,
                size: 64,
                color: theme.colorScheme.primary,
              ),
              const SizedBox(height: 16),
              Text(
                'JARVIS',
                textAlign: TextAlign.center,
                style: theme.textTheme.headlineMedium?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
              ),
              const SizedBox(height: 8),
              Text(
                'Enter your server URL',
                textAlign: TextAlign.center,
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 32),
              TextField(
                controller: _urlController,
                decoration: InputDecoration(
                  labelText: 'Server URL',
                  hintText: 'http://100.x.x.x:8000',
                  border: const OutlineInputBorder(),
                  prefixIcon: const Icon(Icons.link),
                  errorText: _error,
                ),
                keyboardType: TextInputType.url,
                onSubmitted: (_) => _checkAndProceed(),
              ),
              const SizedBox(height: 8),
              Text(
                'Use your Tailscale IP address\nto connect from mobile.',
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 24),
              FilledButton(
                onPressed: _loading ? null : _checkAndProceed,
                child: _loading
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Connect'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
