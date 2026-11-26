import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/network/vault_api.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

final selectionRepositoryProvider = Provider<SelectionRepository>((ref) => SelectionRepository(
      ref.watch(apiClientProvider),
      ref.watch(vaultApiProvider),
    ));

/// What saving outside text would write: the note (kept word for word) and facts about you for Profile.
class IngestPlan {
  IngestPlan(this.notePath, this.noteContent, this.facts);

  final String notePath;
  final String noteContent;

  /// (Profile file, fact) pairs.
  final List<(String, String)> facts;
}

/// Selected or shared text, handled by Jarvis on the PC (the vault's brain: the local model, never a
/// cloud one): explained, summarised, answered about, or saved where it belongs in the vault.
class SelectionRepository {
  SelectionRepository(this._api, this._vault);

  final ApiClient _api;
  final VaultApi _vault;

  /// The local model on [text]: mode "explain", "summarise" or "ask" (with [question]), streamed.
  Stream<String> explain(String text, String mode, {String question = ''}) async* {
    final response = await _session(() => _api.dio.post(
          '/ask/ai/explain',
          data: {'text': text, 'mode': mode, 'question': question},
          options: Options(responseType: ResponseType.stream, receiveTimeout: const Duration(minutes: 3)),
        ));
    // NDJSON; a TCP chunk can split a line, so decode to newline boundaries.
    final lines = (response.data.stream as Stream).map((chunk) => List<int>.from(chunk)).transform(utf8.decoder).transform(const LineSplitter());
    await for (final line in lines) {
      if (line.trim().isEmpty) continue;
      final Object? event;
      try {
        event = jsonDecode(line);
      } catch (_) {
        continue;
      }
      if (event is! Map) continue;
      if (event['error'] is String) throw SelectionException(event['error'] as String);
      if (event['token'] is String) yield event['token'] as String;
    }
  }

  /// Where the text belongs, decided by the vault's brain. Nothing is written yet.
  Future<IngestPlan> plan(String text, String source, String? url) async {
    try {
      final response = await _session(() => _api.dio.post(
            '/ask/ingest/plan',
            data: {'text': text, 'source': source, 'url': url},
            options: Options(receiveTimeout: const Duration(minutes: 3)),
          ));
      final data = response.data as Map;
      final note = data['note'] as Map;
      return IngestPlan(note['path'] as String, note['content'] as String, [
        for (final fact in (data['facts'] as List? ?? const []).whereType<Map>()) (fact['path'] as String, fact['fact'] as String),
      ]);
    } on DioException catch (e) {
      final detail = (e.response?.data is Map) ? (e.response!.data as Map)['detail'] : null;
      throw SelectionException(detail is String ? 'Not saved: $detail.' : 'Your PC couldn\'t be reached, so nothing was saved.');
    }
  }

  /// Writes the plan (through the files API, versioned, so every device syncs it) and reindexes.
  Future<void> save(IngestPlan plan) async {
    await _write(plan.notePath, plan.noteContent);
    final byFile = <String, List<String>>{};
    for (final (path, fact) in plan.facts) {
      byFile.putIfAbsent(path, () => []).add(fact);
    }
    for (final entry in byFile.entries) {
      var text = await _read(entry.key);
      for (final fact in entry.value) {
        text = addFact(text, fact, entry.key);
      }
      await _write(entry.key, text ?? '');
    }
    await _session(() => _api.dio.post('/ask/reindex')).catchError((_) => Response(requestOptions: RequestOptions())); // findable at once
  }

  /// A fact appended to a Profile file; a new file starts with its heading (the PC's ProfileNotes.Add).
  static String addFact(String? text, String fact, String path) {
    final clean = fact.split(RegExp(r'\s+')).where((word) => word.isNotEmpty).join(' ');
    if (clean.isEmpty) return text ?? '';
    if (text == null || text.trim().isEmpty) {
      final name = path.split('/').last.replaceAll('.md', '').replaceAll('-', ' ');
      return '# ${name.isEmpty ? 'Notes' : name[0].toUpperCase() + name.substring(1)}\n\n- $clean\n';
    }
    return '${text.endsWith('\n') ? text : '$text\n'}- $clean\n';
  }

  Future<String?> _read(String path) async {
    try {
      final response = await _session(() => _api.dio.get('/files/${Uri.encodeFull(path)}'));
      return (response.data as Map)['content'] as String?;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  Future<void> _write(String path, String content) async {
    final encoded = Uri.encodeFull(path);
    try {
      await _session(() => _api.dio.put('/files/$encoded', data: {'content': content}));
    } on DioException catch (e) {
      if (e.response?.statusCode != 404) rethrow;
      await _session(() => _api.dio.post('/files/$encoded', data: {'content': content, 'type': 'file'}));
    }
  }

  Future<Response<dynamic>> _session(Future<Response<dynamic>> Function() request) => _vault.call(request);
}

class SelectionException implements Exception {
  SelectionException(this.message);

  final String message;

  @override
  String toString() => message;
}
