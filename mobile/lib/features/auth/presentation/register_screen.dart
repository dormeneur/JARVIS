import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

/// Screen for registering a device with the server.
/// Supports both first-device (setup_secret) and additional device (existing JWT) flows.
class RegisterScreen extends ConsumerStatefulWidget {
  final String serverUrl;

  const RegisterScreen({super.key, required this.serverUrl});

  @override
  ConsumerState<RegisterScreen> createState() => _RegisterScreenState();
}

class _RegisterScreenState extends ConsumerState<RegisterScreen> {
  final _deviceNameController = TextEditingController();
  final _secretController = TextEditingController();
  bool _isFirstDevice = true;
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
      if (_isFirstDevice) {
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
      } else {
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
              SegmentedButton<bool>(
                segments: const [
                  ButtonSegment(value: true, label: Text('First Device')),
                  ButtonSegment(value: false, label: Text('Additional Device')),
                ],
                selected: {_isFirstDevice},
                onSelectionChanged: (v) =>
                    setState(() => _isFirstDevice = v.first),
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
                  labelText: _isFirstDevice ? 'Setup secret' : 'Existing JWT',
                  hintText: _isFirstDevice
                      ? 'From docker logs jv-api'
                      : 'Bearer token from another device',
                  border: const OutlineInputBorder(),
                  prefixIcon: const Icon(Icons.key),
                ),
                maxLines: _isFirstDevice ? 1 : 3,
              ),
              if (_error != null) ...[
                const SizedBox(height: 12),
                Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
              ],
              const SizedBox(height: 24),
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
}
