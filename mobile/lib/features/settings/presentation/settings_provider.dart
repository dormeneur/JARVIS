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
