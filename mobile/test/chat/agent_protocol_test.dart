import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/features/chat/data/agent_models.dart';
import 'package:jarvis_mobile/features/chat/presentation/tool_permission_provider.dart';
import 'package:jarvis_mobile/features/chat/presentation/widgets/tool_permission_sheet.dart';
import 'package:shared_preferences/shared_preferences.dart';

void main() {
  group('AgentEvent decoding', () {
    AgentEvent? decode(Map<String, dynamic> j) =>
        AgentEvent.fromJson(jsonDecode(jsonEncode(j)) as Map<String, dynamic>);

    test('decodes each event type the server emits', () {
      expect(decode({'type': 'token', 'token': 'hi'}), isA<AgentToken>());
      expect(
        decode({'type': 'tool_start', 'id': 'c1', 'name': 'read_file', 'arguments': {}}),
        isA<AgentToolStart>(),
      );
      expect(
        decode({'type': 'tool_result', 'id': 'c1', 'ok': true, 'summary': 'x'}),
        isA<AgentToolResult>(),
      );
      expect(
        decode({'type': 'approval_required', 'id': 'c1', 'name': 'delete_file', 'arguments': {}, 'risk': 'delete'}),
        isA<AgentApprovalRequired>(),
      );
      expect(decode({'type': 'final', 'answer': 'a', 'sources': []}), isA<AgentFinal>());
      expect(decode({'type': 'error', 'error': 'boom'}), isA<AgentError>());
    });

    test('unknown event types are ignored, not fatal', () {
      // Forward compatibility: a newer server must not break an older client.
      expect(decode({'type': 'something_new', 'x': 1}), isNull);
    });

    test('missing fields fall back instead of throwing', () {
      final e = decode({'type': 'tool_start'});
      expect(e, isA<AgentToolStart>());
      expect((e as AgentToolStart).call.name, '');
    });

    test('approval risk maps to the right class', () {
      final e = decode({
        'type': 'approval_required',
        'id': 'c1',
        'name': 'delete_file',
        'arguments': {'path': 'a.md'},
        'risk': 'delete',
      }) as AgentApprovalRequired;
      expect(e.call.risk, ToolRisk.delete);
    });

    test('unknown risk string defaults to write, never read', () {
      // An unrecognised risk must not silently become auto-approved.
      expect(toolRiskFrom('bogus'), ToolRisk.write);
      expect(toolRiskFrom(null), ToolRisk.write);
    });
  });

  group('ToolCall display', () {
    test('subject prefers path, then source, then query', () {
      expect(
        const ToolCall(id: 'a', name: 'create_file', arguments: {'path': 'x.md'}).subject,
        'x.md',
      );
      expect(
        const ToolCall(id: 'a', name: 'move_file', arguments: {'source': 's.md', 'destination': 'd'}).subject,
        's.md',
      );
      expect(
        const ToolCall(id: 'a', name: 'search_vault', arguments: {'query': 'taxes'}).subject,
        'taxes',
      );
    });

    test('transcript json carries the approval decision', () {
      const call = ToolCall(id: 'a', name: 'create_file', arguments: {'path': 'x.md'});
      expect(call.toTranscriptJson(true)['approved'], isTrue);
      expect(call.toTranscriptJson(false)['approved'], isFalse);
      expect(call.toTranscriptJson(true)['name'], 'create_file');
    });
  });

  group('ToolPermissions', () {
    setUp(() => SharedPreferences.setMockInitialValues({}));

    test('nothing is granted by default', () {
      const p = ToolPermissions();
      expect(p.granted, isEmpty);
      expect(p.isGranted('create_file'), isFalse);
    });

    test('allow-once grants nothing persistent', () async {
      final n = ToolPermissionNotifier();
      const call = ToolCall(id: 'a', name: 'create_file', arguments: {});
      expect(await n.applyDecision(call, ToolDecision.once), isTrue);
      expect(n.state.isGranted('create_file'), isFalse);
    });

    test('session grant persists across calls but not restarts', () async {
      final n = ToolPermissionNotifier();
      const call = ToolCall(id: 'a', name: 'create_file', arguments: {});
      await n.applyDecision(call, ToolDecision.session);
      expect(n.state.isGranted('create_file'), isTrue);
      expect(n.state.always, isEmpty);

      n.clearSession();
      expect(n.state.isGranted('create_file'), isFalse);
    });

    test('always grant is written to storage', () async {
      final n = ToolPermissionNotifier();
      const call = ToolCall(id: 'a', name: 'create_file', arguments: {});
      await n.applyDecision(call, ToolDecision.always);
      expect(n.state.always, contains('create_file'));

      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getStringList('jarvis_always_allowed_tools'), contains('create_file'));
    });

    test('deny grants nothing and blocks the call', () async {
      final n = ToolPermissionNotifier();
      const call = ToolCall(id: 'a', name: 'delete_file', arguments: {}, risk: ToolRisk.delete);
      expect(await n.applyDecision(call, ToolDecision.deny), isFalse);
      expect(n.state.granted, isEmpty);
    });

    test('destructive tools are never remembered', () async {
      // A standing grant on delete would let the model wipe files silently.
      final n = ToolPermissionNotifier();
      const del = ToolCall(id: 'a', name: 'delete_file', arguments: {}, risk: ToolRisk.delete);

      expect(await n.applyDecision(del, ToolDecision.session), isTrue);
      expect(n.state.isGranted('delete_file'), isFalse);

      expect(await n.applyDecision(del, ToolDecision.always), isTrue);
      expect(n.state.isGranted('delete_file'), isFalse);
    });

    test('revokeAll clears both scopes and storage', () async {
      final n = ToolPermissionNotifier();
      await n.applyDecision(
        const ToolCall(id: 'a', name: 'create_file', arguments: {}),
        ToolDecision.always,
      );
      await n.applyDecision(
        const ToolCall(id: 'b', name: 'edit_file', arguments: {}),
        ToolDecision.session,
      );
      await n.revokeAll();

      expect(n.state.granted, isEmpty);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getStringList('jarvis_always_allowed_tools'), isEmpty);
    });
  });
}
