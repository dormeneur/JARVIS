import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:intl/intl.dart';

final deviceListProvider = FutureProvider.autoDispose<List<Map<String, dynamic>>>((ref) async {
  final authRepo = ref.watch(authRepositoryProvider);
  return await authRepo.listDevices();
});

class DeviceManagementScreen extends ConsumerWidget {
  const DeviceManagementScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final devicesAsync = ref.watch(deviceListProvider);
    final authRepo = ref.watch(authRepositoryProvider);
    final currentDeviceId = ref.watch(authProvider.select((s) => s.deviceId));
    final isAuthorized = ref.watch(authProvider.select((s) => s.isSecretsAuthorized));

    return Scaffold(
      appBar: AppBar(
        title: const Text('Device Management'),
        actions: [
          IconButton(
            icon: const Icon(Icons.refresh),
            onPressed: () => ref.refresh(deviceListProvider),
          ),
        ],
      ),
      body: devicesAsync.when(
        data: (devices) => ListView.builder(
          itemCount: devices.length,
          itemBuilder: (context, index) {
            final device = devices[index];
            final deviceId = device['device_id'] as String;
            final isCurrent = deviceId == currentDeviceId;
            final isSecretsAuth = device['is_secrets_authorized'] as bool;
            final registeredAt = DateTime.parse(device['registered_at'] as String);

            return ListTile(
              leading: Icon(
                isCurrent ? Icons.phone_android : Icons.devices,
                color: isCurrent ? Theme.of(context).primaryColor : null,
              ),
              title: Text(
                '${device['device_name']}${isCurrent ? ' (This Device)' : ''}',
                style: TextStyle(
                  fontWeight: isCurrent ? FontWeight.bold : FontWeight.normal,
                ),
              ),
              subtitle: Text(
                'Registered: ${DateFormat.yMMMd().add_jm().format(registeredAt.toLocal())}',
              ),
              trailing: isSecretsAuth
                  ? const Chip(
                      label: Text('Authorized'),
                      backgroundColor: Colors.greenAccent,
                    )
                  : isAuthorized
                      ? ElevatedButton(
                          onPressed: () async {
                            try {
                              await authRepo.authorizeSecrets(deviceId);
                              if (!context.mounted) return;
                              ScaffoldMessenger.of(context).showSnackBar(
                                SnackBar(content: Text('Authorized ${device['device_name']}')),
                              );
                              ref.invalidate(deviceListProvider);
                            } catch (e) {
                              if (!context.mounted) return;
                              ScaffoldMessenger.of(context).showSnackBar(
                                SnackBar(content: Text('Failed to authorize: $e')),
                              );
                            }
                          },
                          child: const Text('Authorize'),
                        )
                      : const Text('Unauthorized'),
            );
          },
        ),
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (err, stack) => Center(child: Text('Error: $err')),
      ),
    );
  }
}
