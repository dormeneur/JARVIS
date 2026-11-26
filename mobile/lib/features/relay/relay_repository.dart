import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/vault_api.dart';
import 'package:shared_preferences/shared_preferences.dart';

final relayRepositoryProvider = Provider<RelayRepository>((ref) => RelayRepository(ref.watch(vaultApiProvider)));

/// Kept for the whole app run: it notifies when a job this phone sent ends.
final relayWatcherProvider = Provider<RelayWatcher>((ref) {
  final repo = ref.watch(relayRepositoryProvider);
  final watcher = RelayWatcher(repo.recent, RelayWatcher.notify)..check();
  ref.onDispose(watcher.dispose);
  return watcher;
});

/// A job sent to the PC (jv-api /relay/jobs): queued until the desktop app claims it, then
/// running, then finished or failed with the result.
class RelayJob {
  RelayJob.fromJson(Map json)
      : id = json['id'] as String,
        title = json['title'] as String? ?? '',
        brief = json['brief'] as String? ?? '',
        status = json['status'] as String? ?? 'queued',
        result = json['result'] as String?,
        from = json['from'] as String? ?? '',
        updated = DateTime.tryParse(json['updated'] as String? ?? '')?.toLocal();

  final String id;
  final String title;
  final String brief;
  final String status;
  final String? result;
  final String from;
  final DateTime? updated;

  bool get ended => status == 'finished' || status == 'failed';
}

class RelayRepository {
  RelayRepository(this._vault);

  final VaultApi _vault;

  /// Throws a DioException with 422 for a brief that looks like it holds a secret.
  Future<RelayJob> send(String brief) async =>
      RelayJob.fromJson((await _vault.call(() => _vault.dio.post('/relay/jobs', data: {'brief': brief}))).data as Map);

  Future<List<RelayJob>> recent() async {
    final data = (await _vault.call(() => _vault.dio.get('/relay/jobs'))).data as Map;
    return [for (final j in (data['jobs'] as List? ?? const []).whereType<Map>()) if (j['id'] is String) RelayJob.fromJson(j)];
  }
}

/// Watches the jobs this phone sent and shows a notification when one ends. No push service: it
/// checks every 20 s while the app is alive and something is pending, and once at start, so a job
/// that ended while the app was closed is announced when it's next opened.
///
/// ponytail: in-process polling; add a WorkManager job if notifications with the app closed are missed.
class RelayWatcher {
  RelayWatcher(this._recent, this._notify);

  static const _key = 'relay_pending';
  static const _channel = MethodChannel('jarvis/notify');

  final Future<List<RelayJob>> Function() _recent;
  final Future<void> Function(RelayJob) _notify;
  Timer? _timer;
  bool _checking = false;

  /// Android's notification permission prompt (13+), asked when the first job is sent.
  static Future<void> askPermission() async {
    try {
      await _channel.invokeMethod('ask');
    } on MissingPluginException {
      // Not on Android.
    }
  }

  static Future<void> notify(RelayJob job) async {
    final body = (job.result ?? '').trim();
    try {
      await _channel.invokeMethod('show', {
        'id': job.id,
        'title': job.status == 'finished' ? 'Done on your PC: ${job.title}' : "Didn't finish on your PC: ${job.title}",
        'body': body.length > 400 ? '${body.substring(0, 400)}…' : body,
      });
    } on MissingPluginException {
      // Not on Android.
    }
  }

  Future<void> track(String id) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setStringList(_key, {...?prefs.getStringList(_key), id}.toList());
    _timer ??= Timer.periodic(const Duration(seconds: 20), (_) => check());
  }

  Future<void> check() async {
    if (_checking) return;
    _checking = true;
    try {
      final prefs = await SharedPreferences.getInstance();
      final pending = prefs.getStringList(_key) ?? const [];
      if (pending.isEmpty) {
        _stop();
        return;
      }
      final List<RelayJob> jobs;
      try {
        jobs = await _recent();
      } catch (_) {
        _timer ??= Timer.periodic(const Duration(seconds: 20), (_) => check());
        return; // offline: try again on the next tick
      }
      final byId = {for (final j in jobs) j.id: j};
      final still = <String>[];
      for (final id in pending) {
        final job = byId[id];
        if (job == null) continue; // aged off the server's list
        if (job.ended) {
          await _notify(job);
        } else {
          still.add(id);
        }
      }
      await prefs.setStringList(_key, still);
      if (still.isEmpty) {
        _stop();
      } else {
        _timer ??= Timer.periodic(const Duration(seconds: 20), (_) => check());
      }
    } finally {
      _checking = false;
    }
  }

  void _stop() {
    _timer?.cancel();
    _timer = null;
  }

  void dispose() => _stop();
}
