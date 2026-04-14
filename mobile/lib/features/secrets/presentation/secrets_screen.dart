import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/secrets/presentation/secrets_provider.dart';
import 'package:jarvis_mobile/core/storage/app_database.dart';

class SecretsScreen extends ConsumerStatefulWidget {
  const SecretsScreen({super.key});

  @override
  ConsumerState<SecretsScreen> createState() => _SecretsScreenState();
}

class _SecretsScreenState extends ConsumerState<SecretsScreen> {
  final _pinController = TextEditingController();
  final _confirmPinController = TextEditingController();
  final _labelController = TextEditingController();
  final _valueController = TextEditingController();

  @override
  void dispose() {
    _pinController.dispose();
    _confirmPinController.dispose();
    _labelController.dispose();
    _valueController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(secretsProvider);
    
    // User interaction wrapper for auto-lock reset
    return GestureDetector(
      behavior: HitTestBehavior.translucent,
      onTap: () => ref.read(secretsProvider.notifier).resetLockTimer(),
      onPanDown: (_) => ref.read(secretsProvider.notifier).resetLockTimer(),
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Secrets Vault'),
          actions: [
            if (state.isUnlocked)
              IconButton(
                icon: const Icon(Icons.lock_open),
                onPressed: () => ref.read(secretsProvider.notifier).lock(),
                tooltip: 'Lock now',
              ),
          ],
        ),
        body: _buildBody(context, state),
      ),
    );
  }

  Widget _buildBody(BuildContext context, SecretsState state) {
    if (state.isLoading) {
      return const Center(child: CircularProgressIndicator());
    }

    if (!state.hasPin) {
      return _buildSetupPin(context);
    }

    if (!state.isUnlocked) {
      return _buildUnlock(context, state);
    }

    return _buildSecretsList(context, state);
  }

  Widget _buildSetupPin(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24.0),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.security, size: 64, color: Colors.blue),
          const SizedBox(height: 24),
          Text(
            'Secure your secrets',
            style: Theme.of(context).textTheme.headlineSmall,
          ),
          const SizedBox(height: 8),
          const Text(
            'Set a PIN to encrypt your credentials. This PIN is never stored and cannot be recovered.',
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 32),
          TextField(
            controller: _pinController,
            decoration: const InputDecoration(
              labelText: 'Enter PIN',
              border: OutlineInputBorder(),
            ),
            obscureText: true,
            keyboardType: TextInputType.number,
          ),
          const SizedBox(height: 16),
          TextField(
            controller: _confirmPinController,
            decoration: const InputDecoration(
              labelText: 'Confirm PIN',
              border: OutlineInputBorder(),
            ),
            obscureText: true,
            keyboardType: TextInputType.number,
          ),
          const SizedBox(height: 24),
          SizedBox(
            width: double.infinity,
            height: 50,
            child: FilledButton(
              onPressed: () {
                if (_pinController.text.length < 4) {
                  ScaffoldMessenger.of(context).showSnackBar(
                    const SnackBar(content: Text('PIN must be at least 4 digits')),
                  );
                  return;
                }
                if (_pinController.text != _confirmPinController.text) {
                  ScaffoldMessenger.of(context).showSnackBar(
                    const SnackBar(content: Text('PINs do not match')),
                  );
                  return;
                }
                ref.read(secretsProvider.notifier).setupPin(_pinController.text);
              },
              child: const Text('Initialize Vault'),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildUnlock(BuildContext context, SecretsState state) {
    return Padding(
      padding: const EdgeInsets.all(24.0),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          const Icon(Icons.lock_outline, size: 64, color: Colors.blue),
          const SizedBox(height: 24),
          Text(
            'Vault Locked',
            style: Theme.of(context).textTheme.headlineSmall,
          ),
          const SizedBox(height: 32),
          TextField(
            controller: _pinController,
            decoration: InputDecoration(
              labelText: 'Enter PIN',
              border: const OutlineInputBorder(),
              errorText: state.error,
            ),
            obscureText: true,
            keyboardType: TextInputType.number,
            onSubmitted: (value) => ref.read(secretsProvider.notifier).unlock(value),
          ),
          const SizedBox(height: 24),
          SizedBox(
            width: double.infinity,
            height: 50,
            child: FilledButton(
              onPressed: () => ref.read(secretsProvider.notifier).unlock(_pinController.text),
              child: const Text('Unlock'),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildSecretsList(BuildContext context, SecretsState state) {
    if (state.secrets.isEmpty) {
      return Center(
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: [
            const Icon(Icons.no_encryption_gmailerrorred, size: 48, color: Colors.grey),
            const SizedBox(height: 16),
            const Text('No secrets found'),
            const SizedBox(height: 24),
            ElevatedButton.icon(
              onPressed: () => _showAddSecretDialog(context),
              icon: const Icon(Icons.add),
              label: const Text('Add Secret'),
            ),
          ],
        ),
      );
    }

    return ListView.builder(
      itemCount: state.secrets.length,
      itemBuilder: (context, index) {
        final secret = state.secrets[index];
        return ListTile(
          leading: const Icon(Icons.vpn_key_outlined),
          title: Text(secret.label),
          subtitle: const Text('********'),
          trailing: IconButton(
            icon: const Icon(Icons.copy),
            onPressed: () {
              final value = ref.read(secretsProvider.notifier).decryptValue(secret);
              ref.read(secretsProvider.notifier).copyToClipboard(value);
              ScaffoldMessenger.of(context).showSnackBar(
                const SnackBar(
                  content: Text('Copied to clipboard. Clears in 30s.'),
                  duration: Duration(seconds: 2),
                ),
              );
            },
          ),
          onTap: () => _showSecretDetails(context, secret),
        );
      },
    );
  }

  void _showAddSecretDialog(BuildContext context) {
    _labelController.clear();
    _valueController.clear();
    showDialog(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('New Secret'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: _labelController,
              decoration: const InputDecoration(labelText: 'Label (e.g. Gmail)'),
              autofocus: true,
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _valueController,
              decoration: const InputDecoration(labelText: 'Secret Value'),
              obscureText: true,
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          TextButton(
            onPressed: () {
              if (_labelController.text.isNotEmpty && _valueController.text.isNotEmpty) {
                ref.read(secretsProvider.notifier).addSecret(
                  _labelController.text,
                  _valueController.text,
                );
                Navigator.pop(context);
              }
            },
            child: const Text('Save'),
          ),
        ],
      ),
    );
  }

  void _showSecretDetails(BuildContext context, SecretEntry secret) {
    final value = ref.read(secretsProvider.notifier).decryptValue(secret);
    showModalBottomSheet(
      context: context,
      builder: (context) => Padding(
        padding: const EdgeInsets.all(24.0),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.vpn_key_outlined),
                const SizedBox(width: 12),
                Text(secret.label, style: Theme.of(context).textTheme.titleLarge),
                const Spacer(),
                IconButton(
                  icon: const Icon(Icons.delete_outline, color: Colors.red),
                  onPressed: () {
                    Navigator.pop(context);
                    _confirmDelete(context, secret);
                  },
                ),
              ],
            ),
            const SizedBox(height: 24),
            const Text('Value:', style: TextStyle(fontWeight: FontWeight.bold)),
            const SizedBox(height: 8),
            SelectableText(
              value,
              style: const TextStyle(fontSize: 18, fontFamily: 'monospace'),
            ),
            const SizedBox(height: 32),
            SizedBox(
              width: double.infinity,
              child: FilledButton.icon(
                onPressed: () {
                  ref.read(secretsProvider.notifier).copyToClipboard(value);
                  Navigator.pop(context);
                  ScaffoldMessenger.of(context).showSnackBar(
                    const SnackBar(content: Text('Copied to clipboard')),
                  );
                },
                icon: const Icon(Icons.copy),
                label: const Text('Copy and Close'),
              ),
            ),
            const SizedBox(height: 16),
          ],
        ),
      ),
    );
  }

  void _confirmDelete(BuildContext context, SecretEntry secret) {
    showDialog(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Delete Secret?'),
        content: Text('Are you sure you want to delete "${secret.label}"?'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          TextButton(
            onPressed: () {
              ref.read(secretsProvider.notifier).deleteSecret(secret.id);
              Navigator.pop(context);
            },
            child: const Text('Delete', style: TextStyle(color: Colors.red)),
          ),
        ],
      ),
    );
  }
}
