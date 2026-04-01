import 'dart:convert';
import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../../../core/network/api_client.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

final chatRepositoryProvider = Provider<ChatRepository>((ref) {
  final apiClient = ref.watch(apiClientProvider);
  return ChatRepository(apiClient);
});

class ChatRepository {
  final ApiClient _apiClient;

  ChatRepository(this._apiClient);

  /// Streams the AI response by parsing NDJSON chunks from the backend.
  /// Yields text fragments continuously, and finally a JSON string with sources.
  Stream<String> askJarvis(String query, {List<String>? attachments}) async* {
    try {
      final body = <String, dynamic>{
        'query': query,
        'options': {'stream': true},
      };
      if (attachments != null && attachments.isNotEmpty) {
        body['attachments'] = attachments;
      }

      final response = await _apiClient.dio.post(
        '/ask',
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
}
