/// Wire types for the agentic chat protocol (NDJSON from /ask/ai/agent).
library;

/// How dangerous a tool is. Drives what the permission sheet offers.
enum ToolRisk { read, write, delete }

ToolRisk toolRiskFrom(String? raw) => switch (raw) {
      'read' => ToolRisk.read,
      'delete' => ToolRisk.delete,
      _ => ToolRisk.write,
    };

/// One tool invocation the model chose to make.
class ToolCall {
  final String id;
  final String name;
  final Map<String, dynamic> arguments;
  final ToolRisk risk;

  /// null while running, then true/false once the result arrives.
  final bool? ok;
  final String? summary;
  final bool denied;

  const ToolCall({
    required this.id,
    required this.name,
    required this.arguments,
    this.risk = ToolRisk.write,
    this.ok,
    this.summary,
    this.denied = false,
  });

  ToolCall copyWith({bool? ok, String? summary, bool? denied}) => ToolCall(
        id: id,
        name: name,
        arguments: arguments,
        risk: risk,
        ok: ok ?? this.ok,
        summary: summary ?? this.summary,
        denied: denied ?? this.denied,
      );

  bool get isRunning => ok == null && !denied;

  /// Short human phrasing for the tool card, e.g. `create_file` on a path.
  String get subject {
    for (final key in ['path', 'source', 'query']) {
      final v = arguments[key];
      if (v is String && v.isNotEmpty) return v;
    }
    return arguments.values.whereType<String>().firstOrNull ?? '';
  }

  String get label => switch (name) {
        'create_file' => 'Create file',
        'edit_file' => 'Edit file',
        'move_file' => 'Move',
        'delete_file' => 'Delete',
        'read_file' => 'Read file',
        'list_directory' => 'List folder',
        'search_vault' => 'Search vault',
        _ => name,
      };

  Map<String, dynamic> toTranscriptJson(bool approved) => {
        'name': name,
        'arguments': arguments,
        'approved': approved,
      };
}

/// A decoded line from the agent stream.
sealed class AgentEvent {
  const AgentEvent();

  static AgentEvent? fromJson(Map<String, dynamic> j) {
    switch (j['type'] as String?) {
      case 'token':
        return AgentToken(j['token'] as String? ?? '');
      case 'tool_start':
        return AgentToolStart(ToolCall(
          id: j['id'] as String? ?? '',
          name: j['name'] as String? ?? '',
          arguments: Map<String, dynamic>.from(j['arguments'] as Map? ?? {}),
        ));
      case 'tool_result':
        return AgentToolResult(
          id: j['id'] as String? ?? '',
          ok: j['ok'] as bool? ?? false,
          summary: j['summary'] as String? ?? '',
        );
      case 'approval_required':
        return AgentApprovalRequired(ToolCall(
          id: j['id'] as String? ?? '',
          name: j['name'] as String? ?? '',
          arguments: Map<String, dynamic>.from(j['arguments'] as Map? ?? {}),
          risk: toolRiskFrom(j['risk'] as String?),
        ));
      case 'final':
        return AgentFinal(
          answer: j['answer'] as String? ?? '',
          sources: (j['sources'] as List?) ?? const [],
        );
      case 'error':
        return AgentError(j['error']?.toString() ?? 'Unknown error');
      default:
        // Tolerate unknown future event types rather than breaking the stream.
        return null;
    }
  }
}

class AgentToken extends AgentEvent {
  final String token;
  const AgentToken(this.token);
}

class AgentToolStart extends AgentEvent {
  final ToolCall call;
  const AgentToolStart(this.call);
}

class AgentToolResult extends AgentEvent {
  final String id;
  final bool ok;
  final String summary;
  const AgentToolResult({required this.id, required this.ok, required this.summary});
}

class AgentApprovalRequired extends AgentEvent {
  final ToolCall call;
  const AgentApprovalRequired(this.call);
}

class AgentFinal extends AgentEvent {
  final String answer;
  final List<dynamic> sources;
  const AgentFinal({required this.answer, required this.sources});
}

class AgentError extends AgentEvent {
  final String message;
  const AgentError(this.message);
}
