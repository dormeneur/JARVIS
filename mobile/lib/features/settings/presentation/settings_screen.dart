import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart';
import 'package:jarvis_mobile/core/network/api_exceptions.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/explorer/presentation/explorer_provider.dart';
import 'package:jarvis_mobile/features/settings/presentation/settings_provider.dart';
import 'package:jarvis_mobile/features/settings/presentation/device_management_screen.dart';
import 'package:jarvis_mobile/features/settings/presentation/app_lock.dart';
import 'package:jarvis_mobile/features/chat/presentation/tool_permission_provider.dart';

/// Settings screen — shows device info, server URL, and logout.
class SettingsScreen extends ConsumerWidget {
  const SettingsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final authState = ref.watch(authProvider);
    final showHidden = ref.watch(showHiddenFilesProvider);
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(title: const Text('Settings')),
      body: ListView(
        children: [
          const SizedBox(height: 8),
          _SectionHeader('Connection'),
          ListTile(
            leading: const Icon(Icons.dns_outlined),
            title: const Text('Server URL'),
            subtitle: Text(authState.serverUrl ?? 'Not configured'),
            trailing: const Icon(Icons.edit_outlined),
            onTap: () => _editServerUrl(context, ref),
          ),
          const Divider(),
          _SectionHeader('Device'),
          ListTile(
            leading: const Icon(Icons.phone_android),
            title: const Text('Device Name'),
            subtitle: Text(authState.deviceName ?? 'Unknown'),
          ),
          ListTile(
            leading: const Icon(Icons.fingerprint),
            title: const Text('Device ID'),
            subtitle: Text(authState.deviceId ?? 'Unknown'),
          ),
          ListTile(
            leading: const Icon(Icons.manage_accounts_outlined),
            title: const Text('Device Management'),
            subtitle: const Text('Authorized devices for Secrets'),
            trailing: const Icon(Icons.chevron_right),
            onTap: () {
              Navigator.push(
                context,
                MaterialPageRoute(builder: (context) => const DeviceManagementScreen()),
              );
            },
          ),
          SwitchListTile(
            secondary: const Icon(Icons.lock_outline),
            title: const Text('App Lock'),
            subtitle: const Text('Fingerprint or device PIN to open JARVIS'),
            value: ref.watch(appLockProvider),
            onChanged: (value) => _toggleAppLock(context, ref, value),
          ),
          if (authState.isSecretsAuthorized) const _BackupTile(),
          const Divider(),
          _SectionHeader('Explorer'),
          SwitchListTile(
            secondary: const Icon(Icons.visibility_outlined),
            title: const Text('Show Hidden Files'),
            subtitle: const Text('Files starting with . (e.g. .gitkeep)'),
            value: showHidden,
            onChanged: (value) {
              ref.read(showHiddenFilesProvider.notifier).state = value;
            },
          ),
          SwitchListTile(
            secondary: const Icon(Icons.bug_report_outlined),
            title: const Text('Dry-Run Mode'),
            subtitle: const Text('Preview AI file manifestations without execution'),
            value: ref.watch(dryRunModeProvider),
            onChanged: (value) {
              ref.read(dryRunModeProvider.notifier).toggle(value);
            },
          ),
          const Divider(),
          _SectionHeader('AI Chat'),
          SwitchListTile(
            secondary: const Icon(Icons.auto_delete_outlined),
            title: const Text('Auto-Archive Old Chats'),
            subtitle: const Text('Archive chats older than 7 days to Memory/Chats/'),
            value: ref.watch(autoArchiveProvider),
            onChanged: (value) {
              ref.read(autoArchiveProvider.notifier).toggle(value);
            },
          ),
          _ToolPermissionsTile(),
          const Divider(),
          _SectionHeader('Account'),
          ListTile(
            leading: Icon(Icons.logout, color: theme.colorScheme.error),
            title: Text(
              'Logout',
              style: TextStyle(color: theme.colorScheme.error),
            ),
            subtitle: const Text('Clear stored credentials'),
            onTap: () => _confirmLogout(context, ref),
          ),
        ],
      ),
    );
  }

  Future<void> _toggleAppLock(BuildContext context, WidgetRef ref, bool enable) async {
    final messenger = ScaffoldMessenger.of(context);
    if (enable && !await canAuthenticate()) {
      messenger.showSnackBar(const SnackBar(
        content: Text('Set up a screen lock or fingerprint on this phone first.'),
      ));
      return;
    }
    // Verify in both directions: enabling proves it works; disabling proves it's you.
    final ok = await authenticateUser(enable ? 'Confirm to turn on App Lock' : 'Confirm to turn off App Lock');
    if (ok) await ref.read(appLockProvider.notifier).set(enable);
  }

  void _editServerUrl(BuildContext context, WidgetRef ref) {
    final controller = TextEditingController(
      text: ref.read(authProvider).serverUrl ?? '',
    );

    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Server URL'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            TextField(
              controller: controller,
              decoration: const InputDecoration(
                labelText: 'URL',
                hintText: 'http://100.x.x.x:8000',
                border: OutlineInputBorder(),
              ),
              keyboardType: TextInputType.url,
            ),
            const SizedBox(height: 8),
            Text(
              'Use your Tailscale IP address.',
              style: Theme.of(ctx).textTheme.bodySmall,
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () async {
              final url = controller.text.trim();
              if (url.isNotEmpty) {
                final secureStorage = ref.read(secureStorageProvider);
                await secureStorage.setServerUrl(url);
                final apiClient = ref.read(apiClientProvider);
                apiClient.setBaseUrl(url);
              }
              if (ctx.mounted) Navigator.pop(ctx);
            },
            child: const Text('Save'),
          ),
        ],
      ),
    );
  }

  void _confirmLogout(BuildContext context, WidgetRef ref) {
    showDialog(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Logout'),
        content: const Text(
          'This will clear all stored credentials.\nYou will need to re-register.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: const Text('Cancel'),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(ctx).colorScheme.error,
            ),
            onPressed: () {
              Navigator.pop(ctx); // close dialog
              Navigator.pop(context); // back to explorer
              ref.read(authProvider.notifier).logout();
            },
            child: const Text('Logout'),
          ),
        ],
      ),
    );
  }
}

class _SectionHeader extends StatelessWidget {
  final String title;

  const _SectionHeader(this.title);

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 4),
      child: Text(
        title,
        style: Theme.of(context).textTheme.labelLarge?.copyWith(
          color: Theme.of(context).colorScheme.primary,
        ),
      ),
    );
  }
}

/// Shows which AI tools have a standing "always allow" grant, and lets the
/// user take it back. Without this, "Always allow" would be a one-way door.
class _ToolPermissionsTile extends ConsumerWidget {
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final perms = ref.watch(toolPermissionProvider);
    final theme = Theme.of(context);
    final always = perms.always.toList()..sort();
    final sessionCount = perms.session.length;

    return ListTile(
      leading: const Icon(Icons.verified_user_outlined),
      title: const Text('AI Tool Permissions'),
      subtitle: Text(
        always.isEmpty && sessionCount == 0
            ? 'JARVIS asks before every action'
            : '${always.length} always-allowed'
                '${sessionCount > 0 ? ', $sessionCount this session' : ''}',
      ),
      trailing: (always.isEmpty && sessionCount == 0)
          ? null
          : TextButton(
              onPressed: () async {
                final confirmed = await showDialog<bool>(
                  context: context,
                  builder: (ctx) => AlertDialog(
                    title: const Text('Revoke tool permissions?'),
                    content: Text(
                      always.isEmpty
                          ? 'JARVIS will ask for approval again on every action.'
                          : 'Removes the standing grant for:\n\n'
                              '${always.map((t) => '• $t').join('\n')}\n\n'
                              'JARVIS will ask for approval again on every action.',
                    ),
                    actions: [
                      TextButton(
                        onPressed: () => Navigator.pop(ctx, false),
                        child: const Text('Cancel'),
                      ),
                      FilledButton(
                        onPressed: () => Navigator.pop(ctx, true),
                        style: FilledButton.styleFrom(
                          backgroundColor: theme.colorScheme.error,
                        ),
                        child: const Text('Revoke all'),
                      ),
                    ],
                  ),
                );
                if (confirmed == true) {
                  await ref.read(toolPermissionProvider.notifier).revokeAll();
                }
              },
              child: const Text('Revoke'),
            ),
    );
  }
}

/// Newest-first list of vault backups on the server (admin devices only).
final _backupsProvider = FutureProvider.autoDispose<List<Map<String, dynamic>>>((ref) async {
  try {
    final res = await ref.read(apiClientProvider).dio.get('/backup');
    return List<Map<String, dynamic>>.from((res.data as Map)['backups'] as List);
  } on DioException catch (e) {
    throw mapDioError(e);
  }
});

class _BackupTile extends ConsumerStatefulWidget {
  const _BackupTile();

  @override
  ConsumerState<_BackupTile> createState() => _BackupTileState();
}

class _BackupTileState extends ConsumerState<_BackupTile> {
  bool _running = false;

  Future<void> _backUpNow() async {
    setState(() => _running = true);
    String message;
    try {
      // Zipping a large vault can outlast the default 30 s receive timeout.
      final res = await ref.read(apiClientProvider).dio.post(
            '/backup/export',
            options: Options(receiveTimeout: const Duration(minutes: 10)),
          );
      final mb = ((res.data as Map)['size_bytes'] as int) / (1024 * 1024);
      message = 'Backup saved on server (${mb.toStringAsFixed(1)} MB)';
      ref.invalidate(_backupsProvider);
    } on DioException catch (e) {
      message = 'Backup failed: ${mapDioError(e).message}';
    }
    if (!mounted) return;
    setState(() => _running = false);
    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(message)));
  }

  @override
  Widget build(BuildContext context) {
    final backups = ref.watch(_backupsProvider);
    final subtitle = backups.when(
      data: (list) => list.isEmpty
          ? 'No backups yet'
          : 'Last: ${DateFormat.yMMMd().add_jm().format(DateTime.parse(list.first['created_at'] as String).toLocal())}'
              ' · ${list.length} kept',
      loading: () => 'Checking…',
      error: (e, _) => 'Unavailable: $e',
    );

    return ListTile(
      leading: const Icon(Icons.backup_outlined),
      title: const Text('Vault Backups'),
      subtitle: Text(subtitle),
      trailing: _running
          ? const SizedBox(width: 24, height: 24, child: CircularProgressIndicator(strokeWidth: 2))
          : TextButton(onPressed: _backUpNow, child: const Text('Back up now')),
    );
  }
}
