import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../data/agent_models.dart';
import 'widgets/tool_permission_sheet.dart';

const _prefsKey = 'jarvis_always_allowed_tools';

/// Tracks which tools may run without asking.
///
/// Two scopes, mirroring Claude Code:
///   * session — cleared when the app restarts, held in memory
///   * always  — persisted to SharedPreferences until revoked in Settings
///
/// Destructive tools are never remembered; [grant] refuses to store them
/// even if a caller asks, so a stray "always" can't authorise silent deletes.
class ToolPermissions {
  final Set<String> session;
  final Set<String> always;

  const ToolPermissions({this.session = const {}, this.always = const {}});

  /// Tools the server may run without pausing for approval.
  List<String> get granted => {...session, ...always}.toList();

  bool isGranted(String tool) => session.contains(tool) || always.contains(tool);

  ToolPermissions copyWith({Set<String>? session, Set<String>? always}) =>
      ToolPermissions(
        session: session ?? this.session,
        always: always ?? this.always,
      );
}

class ToolPermissionNotifier extends StateNotifier<ToolPermissions> {
  ToolPermissionNotifier() : super(const ToolPermissions()) {
    _load();
  }

  Future<void> _load() async {
    final prefs = await SharedPreferences.getInstance();
    final stored = prefs.getStringList(_prefsKey) ?? const [];
    state = state.copyWith(always: stored.toSet());
  }

  Future<void> _persist(Set<String> always) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setStringList(_prefsKey, always.toList());
  }

  /// Record a decision. Returns true if the call should proceed.
  Future<bool> applyDecision(ToolCall call, ToolDecision decision) async {
    switch (decision) {
      case ToolDecision.once:
        return true;
      case ToolDecision.session:
        if (call.risk != ToolRisk.delete) {
          state = state.copyWith(session: {...state.session, call.name});
        }
        return true;
      case ToolDecision.always:
        if (call.risk != ToolRisk.delete) {
          final next = {...state.always, call.name};
          state = state.copyWith(always: next);
          await _persist(next);
        }
        return true;
      case ToolDecision.deny:
        return false;
    }
  }

  Future<void> revokeAlways(String tool) async {
    final next = {...state.always}..remove(tool);
    state = state.copyWith(always: next);
    await _persist(next);
  }

  void clearSession() => state = state.copyWith(session: {});

  Future<void> revokeAll() async {
    state = const ToolPermissions();
    await _persist({});
  }
}

final toolPermissionProvider =
    StateNotifierProvider<ToolPermissionNotifier, ToolPermissions>(
  (ref) => ToolPermissionNotifier(),
);
