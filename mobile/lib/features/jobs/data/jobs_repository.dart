import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

/// Talks to jv-api's `/jobs/*` proxy, which forwards to the jv-jobs runner
/// (DOCUMENTATION/OFFICIAL-DOCS/12-Job-Search-Integration.md).
class JobsRepository {
  final ApiClient _api;
  JobsRepository(this._api);

  Dio get _dio => _api.dio;

  Future<Map<String, dynamic>> status() async =>
      Map<String, dynamic>.from((await _dio.get('/jobs/status')).data as Map);

  Future<List<Map<String, dynamic>>> board() async {
    final data = (await _dio.get('/jobs/board')).data as Map;
    return (data['cards'] as List).map((c) => Map<String, dynamic>.from(c as Map)).toList();
  }

  Future<List<Map<String, dynamic>>> runs() async {
    final data = (await _dio.get('/jobs/runs')).data as Map;
    return (data['runs'] as List).map((r) => Map<String, dynamic>.from(r as Map)).toList();
  }

  Future<Map<String, dynamic>> run(String id) async =>
      Map<String, dynamic>.from((await _dio.get('/jobs/runs/$id')).data as Map);

  Future<Map<String, dynamic>> start(String kind, [Map<String, dynamic> args = const {}]) async =>
      Map<String, dynamic>.from(
          (await _dio.post('/jobs/runs', data: {'kind': kind, 'args': args})).data as Map);

  Future<void> answer(String id, String text) =>
      _dio.post('/jobs/runs/$id/answer', data: {'text': text});

  Future<void> action(String id, String verb) => _dio.post('/jobs/runs/$id/$verb');

  /// Progress events for a run, until it stops being active.
  Stream<Map<String, dynamic>> events(String id) async* {
    final response = await _dio.get(
      '/jobs/runs/$id/events',
      options: Options(responseType: ResponseType.stream, receiveTimeout: const Duration(hours: 1)),
    );
    // A TCP chunk can split a JSON line, so decode to newline boundaries.
    final lines = (response.data.stream as Stream)
        .map((chunk) => List<int>.from(chunk))
        .transform(utf8.decoder)
        .transform(const LineSplitter());
    await for (final line in lines) {
      if (line.trim().isEmpty) continue;
      try {
        final decoded = jsonDecode(line);
        if (decoded is Map<String, dynamic>) yield decoded;
      } catch (_) {}
    }
  }

  /// Copyable text of an artifact. For a PDF this is its text layer.
  Future<String> artifactText(String path) async {
    final data = (await _dio.get('/jobs/artifact/text', queryParameters: {'path': path})).data as Map;
    return data['text'] as String? ?? '';
  }

  /// Downloads an artifact to the temp dir for the PDF viewer.
  Future<File> download(String path) async {
    final dir = await getTemporaryDirectory();
    final file = File('${dir.path}/jobs/${path.replaceAll('/', '_')}');
    await file.parent.create(recursive: true);
    await _dio.download('/jobs/artifact', file.path, queryParameters: {'path': path});
    return file;
  }
}

final jobsRepositoryProvider =
    Provider<JobsRepository>((ref) => JobsRepository(ref.watch(apiClientProvider)));

final jobsBoardProvider = FutureProvider.autoDispose<List<Map<String, dynamic>>>(
    (ref) => ref.watch(jobsRepositoryProvider).board());

final jobsStatusProvider = FutureProvider.autoDispose<Map<String, dynamic>>(
    (ref) => ref.watch(jobsRepositoryProvider).status());

final jobsRunsProvider = FutureProvider.autoDispose<List<Map<String, dynamic>>>(
    (ref) => ref.watch(jobsRepositoryProvider).runs());
