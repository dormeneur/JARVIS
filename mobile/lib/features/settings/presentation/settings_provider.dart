import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:shared_preferences/shared_preferences.dart';

final dryRunModeProvider = StateNotifierProvider<DryRunModeNotifier, bool>((ref) {
  return DryRunModeNotifier();
});

class DryRunModeNotifier extends StateNotifier<bool> {
  DryRunModeNotifier() : super(false) {
    _loadSync();
  }

  Future<void> _loadSync() async {
    final prefs = await SharedPreferences.getInstance();
    state = prefs.getBool('jarvis_dry_run_mode') ?? false;
  }

  Future<void> toggle(bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('jarvis_dry_run_mode', value);
    state = value;
  }
}

/// Whether the auto-archive feature is enabled.
final autoArchiveProvider = StateNotifierProvider<AutoArchiveNotifier, bool>((ref) {
  return AutoArchiveNotifier();
});

class AutoArchiveNotifier extends StateNotifier<bool> {
  AutoArchiveNotifier() : super(false) {
    _load();
  }

  Future<void> _load() async {
    final prefs = await SharedPreferences.getInstance();
    state = prefs.getBool('jarvis_auto_archive') ?? false;
  }

  Future<void> toggle(bool value) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool('jarvis_auto_archive', value);
    state = value;
  }
}

/// Tracks the last time the archive job ran (ISO8601 string or null).
class ArchiveTimestampService {
  static const _key = 'jarvis_last_archive_run';

  static Future<DateTime?> getLastRun() async {
    final prefs = await SharedPreferences.getInstance();
    final value = prefs.getString(_key);
    if (value == null) return null;
    return DateTime.tryParse(value);
  }

  static Future<void> setLastRun(DateTime timestamp) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_key, timestamp.toUtc().toIso8601String());
  }

  /// Returns true if the archive job should run (last run was > 24h ago or never).
  static Future<bool> shouldRunToday() async {
    final lastRun = await getLastRun();
    if (lastRun == null) return true;
    return DateTime.now().toUtc().difference(lastRun).inHours >= 24;
  }
}
