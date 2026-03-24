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
  Stream<String> askJarvis(String query) async* {
    try {
      final response = await _apiClient.dio.post(
        '/ask',
        data: {
          'query': query,
          'options': {'stream': true}
        },
        options: Options(
          responseType: ResponseType.stream,
          receiveTimeout: const Duration(seconds: 120),
        ),
      );

      print('[JARVIS-CHAT] Got response with status: ${response.statusCode}');
      final stream = response.data.stream as Stream;

      // Decode the stream from bytes to strings, then split by newline for NDJSON
      final stringStream = stream
          .map((chunk) => List<int>.from(chunk))
          .transform(utf8.decoder)
          .transform(const LineSplitter());

      print('[JARVIS-CHAT] Starting to read lines from stream...');
      await for (final line in stringStream) {
        print('[JARVIS-CHAT] Received line: $line');
        if (line.trim().isEmpty) continue;

        try {
          final data = jsonDecode(line);
          if (data.containsKey('token')) {
            yield data['token'] as String;
          } else if (data.containsKey('error')) {
            yield jsonEncode({'error': data['error']});
          } else if (data.containsKey('answer')) {
            // Final response payload containing the full answer and sources
            yield jsonEncode(data);
          }
        } catch (e) {
          // Ignore parse errors for partial chunks; Dio's stream usually gives complete lines
          // due to the LineSplitter, so errors here are unexpected
        }
      }
    } on DioException catch (e) {
      print('[JARVIS-CHAT] DioException: ${e.type} - ${e.message}');
      yield jsonEncode({'error': 'Network error: ${e.message}'});
    } catch (e) {
      print('[JARVIS-CHAT] Exception: $e');
      yield jsonEncode({'error': 'Error: $e'});
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
