import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

enum RegistrationMode { first, additional, reconnect }

/// Screen for registering a device with the server.
/// Supports three flows: first-device, additional device, and reconnect to existing.
class RegisterScreen extends ConsumerStatefulWidget {
  final String serverUrl;

  const RegisterScreen({super.key, required this.serverUrl});

  @override
  ConsumerState<RegisterScreen> createState() => _RegisterScreenState();
}

class _RegisterScreenState extends ConsumerState<RegisterScreen> {
  final _deviceNameController = TextEditingController();
  final _secretController = TextEditingController();
  RegistrationMode _mode = RegistrationMode.first;
  bool _loading = false;
  String? _error;

  @override
  void dispose() {
    _deviceNameController.dispose();
    _secretController.dispose();
    super.dispose();
  }

  Future<void> _register() async {
    final deviceName = _deviceNameController.text.trim();
    if (deviceName.isEmpty) {
      setState(() => _error = 'Enter a device name');
      return;
    }

    setState(() {
      _loading = true;
      _error = null;
    });

    final authNotifier = ref.read(authProvider.notifier);

    try {
      if (_mode == RegistrationMode.first) {
        final secret = _secretController.text.trim();
        if (secret.isEmpty) {
          setState(() {
            _loading = false;
            _error = 'Enter the setup secret from server logs';
          });
          return;
        }

        await authNotifier.registerFirst(
          serverUrl: widget.serverUrl,
          deviceName: deviceName,
          setupSecret: secret,
        );
      } else if (_mode == RegistrationMode.additional) {
        final token = _secretController.text.trim();
        if (token.isEmpty) {
          setState(() {
            _loading = false;
            _error = 'Enter the JWT from an authenticated device';
          });
          return;
        }

        await authNotifier.registerAdditional(
          serverUrl: widget.serverUrl,
          existingToken: token,
          deviceName: deviceName,
        );
      } else {
        // Reconnect mode
        final deviceSecret = _secretController.text.trim();
        if (deviceSecret.isEmpty) {
          setState(() {
            _loading = false;
            _error = 'Enter your device secret (from initial registration)';
          });
          return;
        }

        await authNotifier.reconnect(
          serverUrl: widget.serverUrl,
          deviceName: deviceName,
          deviceSecret: deviceSecret,
        );
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          _loading = false;
          _error = e.toString();
        });
      }
      return;
    }

    if (!mounted) return;

    final state = ref.read(authProvider);
    if (state.status == AuthStatus.authenticated) {
      // Pop back to root — app.dart will redirect to home
      Navigator.of(context).popUntil((route) => route.isFirst);
    } else {
      setState(() {
        _loading = false;
        _error = state.error ?? 'Registration failed';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(title: const Text('Register Device')),
      body: SafeArea(
        child: SingleChildScrollView(
          padding: const EdgeInsets.symmetric(horizontal: 32, vertical: 24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Server: ${widget.serverUrl}',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 24),
              SegmentedButton<RegistrationMode>(
                segments: const [
                  ButtonSegment(
                    value: RegistrationMode.first,
                    label: Text('First'),
                  ),
                  ButtonSegment(
                    value: RegistrationMode.additional,
                    label: Text('Additional'),
                  ),
                  ButtonSegment(
                    value: RegistrationMode.reconnect,
                    label: Text('Reconnect'),
                  ),
                ],
                selected: {_mode},
                onSelectionChanged: (v) => setState(() => _mode = v.first),
              ),
              const SizedBox(height: 24),
              // Mode description with background
              Container(
                padding: const EdgeInsets.all(16),
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerHighest.withOpacity(
                    0.5,
                  ),
                  borderRadius: BorderRadius.circular(8),
                  border: Border.all(
                    color: theme.colorScheme.outline.withOpacity(0.5),
                  ),
                ),
                child: Text(
                  _getModeDescription(),
                  style: theme.textTheme.bodyMedium?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                    height: 1.5,
                  ),
                ),
              ),
              const SizedBox(height: 24),
              TextField(
                controller: _deviceNameController,
                decoration: const InputDecoration(
                  labelText: 'Device name',
                  hintText: 'e.g., moto_g84',
                  border: OutlineInputBorder(),
                  prefixIcon: Icon(Icons.phone_android),
                ),
              ),
              const SizedBox(height: 16),
              TextField(
                controller: _secretController,
                decoration: InputDecoration(
                  labelText: _getSecretLabel(),
                  hintText: _getSecretHint(),
                  border: const OutlineInputBorder(),
                  prefixIcon: const Icon(Icons.key),
                ),
                maxLines: _mode == RegistrationMode.additional ? 3 : 1,
              ),
              const SizedBox(height: 16),
              if (_error != null) ...[
                Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
                const SizedBox(height: 16),
              ],
              FilledButton(
                onPressed: _loading ? null : _register,
                child: _loading
                    ? const SizedBox(
                        height: 20,
                        width: 20,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Text('Register'),
              ),
            ],
          ),
        ),
      ),
    );
  }

  String _getSecretLabel() {
    switch (_mode) {
      case RegistrationMode.first:
        return 'Setup Secret';
      case RegistrationMode.additional:
        return 'JWT Token';
      case RegistrationMode.reconnect:
        return 'Device Secret';
    }
  }

  String _getSecretHint() {
    switch (_mode) {
      case RegistrationMode.first:
        return 'e.g., MySecure123Key (from docker logs)';
      case RegistrationMode.additional:
        return 'e.g., eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9... (long token)';
      case RegistrationMode.reconnect:
        return 'e.g., a7f3-9k2m-5b1c (short alphanumeric code)';
    }
  }

  String _getModeDescription() {
    switch (_mode) {
      case RegistrationMode.first:
        return '''Register your first device using the setup secret.

Where to find it:
1. Check the Docker server logs
2. Run: docker logs jv-api
3. Look for a box starting with "JARVIS SETUP SECRET"
4. Copy the code (appears once during server startup)''';

      case RegistrationMode.additional:
        return '''Add another device using a JWT token.

Where to find it:
1. Use any already-registered device with the app open
2. Go to Settings > My Device
3. Tap "Show JWT Token"
4. Copy and paste the full Bearer token here
(The token starts with "eyJ..." and is quite long)''';

      case RegistrationMode.reconnect:
        return '''Reconnect to an existing device (if app was cleared).

Where to find it:
1. Look in your saved notes/passwords from when this device was first registered
2. The device secret is a short code like "a7f3-9k2m-5b1c"
3. It was shown only once after initial registration
4. It's stored in your phone's secure storage if app data wasn't fully wiped''';
    }
  }
}
