import 'dart:convert';
import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../../../core/network/api_client.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'agent_models.dart';
import 'file_manifest_model.dart';

final chatRepositoryProvider = Provider<ChatRepository>((ref) {
  final apiClient = ref.watch(apiClientProvider);
  return ChatRepository(apiClient);
});

class ChatRepository {
  final ApiClient _apiClient;

  ChatRepository(this._apiClient);

  /// Streams the AI response by parsing NDJSON chunks from the backend.
  /// Yields text fragments continuously, and finally a JSON string with sources.
  Stream<String> askJarvis(String query, {List<String>? attachments, List<Map<String, dynamic>>? chatHistory, String currentDirectory = "."}) async* {
    try {
      final body = <String, dynamic>{
        'query': query,
        'current_directory': currentDirectory,
        'options': {'stream': true},
      };
      if (attachments != null && attachments.isNotEmpty) {
        body['attachments'] = attachments;
      }
      if (chatHistory != null && chatHistory.isNotEmpty) {
        body['chat_history'] = chatHistory;
      }

      final response = await _apiClient.dio.post(
        '/ask/ai/query',
        data: body,
        options: Options(
          responseType: ResponseType.stream,
          receiveTimeout: const Duration(seconds: 120),
        ),
      );

      final stream = response.data.stream as Stream;
      final stringStream = stream
          .map((chunk) => List<int>.from(chunk))
          .transform(utf8.decoder)
          .transform(const LineSplitter());

      await for (final line in stringStream) {
        if (line.trim().isEmpty) continue;

        try {
          final data = jsonDecode(line);
          if (data.containsKey('token')) {
            yield data['token'] as String;
          } else if (data.containsKey('error')) {
            yield jsonEncode({'error': data['error']});
          } else if (data.containsKey('answer')) {
            yield jsonEncode(data);
          }
        } catch (_) {
          // Ignore parse errors for partial chunks
        }
      }
    } on DioException catch (e) {
      yield jsonEncode({'error': 'Network error: ${e.message}'});
    } catch (e) {
      yield jsonEncode({'error': 'Error: $e'});
    }
  }

  /// Agentic chat: the model picks its own tools.
  ///
  /// Emits decoded [AgentEvent]s. The stream ends early on
  /// [AgentApprovalRequired] — the caller records the user's decision in
  /// [toolTranscript] and calls this again to resume from that point.
  Stream<AgentEvent> askAgent(
    String query, {
    List<String>? attachments,
    List<Map<String, dynamic>>? chatHistory,
    String currentDirectory = ".",
    List<String> grantedTools = const [],
    List<Map<String, dynamic>> toolTranscript = const [],
    CancelToken? cancelToken,
  }) async* {
    try {
      final body = <String, dynamic>{
        'query': query,
        'current_directory': currentDirectory,
        'granted_tools': grantedTools,
        'tool_transcript': toolTranscript,
      };
      if (attachments != null && attachments.isNotEmpty) {
        body['attachments'] = attachments;
      }
      if (chatHistory != null && chatHistory.isNotEmpty) {
        body['chat_history'] = chatHistory;
      }

      final response = await _apiClient.dio.post(
        '/ask/ai/agent',
        data: body,
        cancelToken: cancelToken,
        options: Options(
          responseType: ResponseType.stream,
          receiveTimeout: const Duration(seconds: 300),
        ),
      );

      final stream = response.data.stream as Stream;
      // A TCP chunk can split a JSON line, so decode to newline boundaries.
      final lines = stream
          .map((chunk) => List<int>.from(chunk))
          .transform(utf8.decoder)
          .transform(const LineSplitter());

      await for (final line in lines) {
        if (line.trim().isEmpty) continue;
        try {
          final decoded = jsonDecode(line);
          if (decoded is! Map<String, dynamic>) continue;
          final event = AgentEvent.fromJson(decoded);
          if (event != null) yield event;
        } catch (_) {
          // Ignore malformed partial lines.
        }
      }
    } on DioException catch (e) {
      if (CancelToken.isCancel(e)) return;
      yield AgentError('Network error: ${e.message}');
    } catch (e) {
      yield AgentError('$e');
    }
  }

  /// Fetch the tool catalogue (names + risk classes) for the permission UI.
  Future<List<Map<String, dynamic>>> getTools() async {
    try {
      final response = await _apiClient.dio.get('/ask/ai/tools');
      final data = response.data as Map<String, dynamic>;
      return List<Map<String, dynamic>>.from(data['tools'] as List? ?? []);
    } catch (_) {
      return [];
    }
  }

  /// Check if the AI backend is available.
  Future<bool> checkAiStatus() async {
    try {
      final response = await _apiClient.dio.get(
        '/ask/status',
        options: Options(receiveTimeout: const Duration(seconds: 5)),
      );
      final data = response.data;
      return data['ollama'] == 'reachable';
    } catch (_) {
      return false;
    }
  }

  /// Trigger a reindex of the AI knowledge base.
  Future<String> triggerReindex() async {
    try {
      final response = await _apiClient.dio.post('/ask/reindex');
      final data = response.data;
      return data['status'] ?? 'unknown';
    } on DioException catch (e) {
      return 'error: ${e.message}';
    } catch (e) {
      return 'error: $e';
    }
  }

  /// Generate a scaffolded manifest of files from a natural language prompt.
  Future<List<FileManifestItem>> generateFiles(String prompt, {String directory = "."}) async {
    final body = {
      'prompt': prompt,
      'current_directory': directory,
    };
    try {
      final response = await _apiClient.dio.post('/ask/generate-files', data: body);
      final dataList = response.data as List;
      return dataList.map((item) => FileManifestItem.fromJson(item)).toList();
    } on DioException catch (e) {
      throw Exception('Network error: ${e.response?.data?['detail'] ?? e.message}');
    } catch (e) {
      throw Exception('Failed to generate files: $e');
    }
  }

  /// Dry-run a scaffolded manifest of files from a natural language prompt without execution intent.
  Future<List<FileManifestItem>> previewFiles(String prompt, {String directory = "."}) async {
    final body = {
      'prompt': prompt,
      'current_directory': directory,
    };
    try {
      final response = await _apiClient.dio.post('/ask/generate-files/dry-run', data: body);
      final dataList = response.data as List;
      return dataList.map((item) => FileManifestItem.fromJson(item)).toList();
    } on DioException catch (e) {
      throw Exception('Network error: ${e.response?.data?['detail'] ?? e.message}');
    } catch (e) {
      throw Exception('Failed to preview files: $e');
    }
  }

  // --- Chat Session & History Backend Sync ---

  /// Get all chat sessions from the backend.
  Future<List<dynamic>> getSessions() async {
    try {
      final response = await _apiClient.dio.get('/ask/chat/sessions');
      return response.data as List<dynamic>;
    } catch (_) {
      return [];
    }
  }

  /// Get full message history for a session from the backend.
  Future<List<dynamic>> getSessionHistory(String sessionId) async {
    try {
      final response = await _apiClient.dio.get('/ask/chat/sessions/$sessionId');
      return response.data as List<dynamic>;
    } catch (_) {
      return [];
    }
  }

  /// Delete a session and its history from the backend.
  Future<void> deleteSession(String sessionId) async {
    try {
      await _apiClient.dio.delete('/ask/chat/sessions/$sessionId');
    } catch (_) {}
  }

  /// Sync a message pair to the brain history backend.
  Future<void> syncMessageToBrain({
    required String sessionId,
    required String query,
    required String response,
    required String timestamp,
  }) async {
    try {
      await _apiClient.dio.post(
        '/ask/chat/sync',
        data: {
          'session_id': sessionId,
          'query': query,
          'response': response,
          'timestamp': timestamp,
        },
      );
    } catch (e) {
      // Non-critical, just logs error
    }
  }

  /// Check if a file exists on the server by requesting its metadata.
  Future<bool> checkFileExists(String filePath) async {
    try {
      final response = await _apiClient.dio.get(
        '/files/$filePath',
        options: Options(receiveTimeout: const Duration(seconds: 5)),
      );
      return response.statusCode == 200;
    } catch (_) {
      return false;
    }
  }
}
