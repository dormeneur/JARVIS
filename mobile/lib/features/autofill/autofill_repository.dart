import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';
import 'package:jarvis_mobile/features/auth/data/auth_repository.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

final autofillRepositoryProvider = Provider<AutofillRepository>((ref) => AutofillRepository(
      ref.watch(apiClientProvider),
      ref.watch(authRepositoryProvider),
      ref.watch(secureStorageProvider),
    ));

/// What the autofill sheet asks of Jarvis on the PC, through jv-api: answers from the vault's
/// brain (local, never a cloud model), and the form memory file to learn into.
class AutofillRepository {
  AutofillRepository(this._api, this._auth, this._storage);

  static const memoryPath = 'Profile/form-memory.json';
  static const resumeFolder = 'Profile/resume';

  final ApiClient _api;
  final AuthRepository _auth;
  final SecureStorage _storage;

  /// Answers for [questions] (id → label) from the vault's brain, as they come: id → answer or
  /// null when the vault doesn't say. Read with the resume, like the PC does.
  Stream<MapEntry<String, String?>> answers(String title, Map<String, String> questions) async* {
    final resumes = await _resumeFiles();
    final response = await _session(() => _api.dio.post(
          '/ask/ai/form-answers',
          data: {
            'title': title,
            'questions': [
              for (final question in questions.entries) {'id': question.key, 'text': question.value, 'kind': 'text', 'options': <String>[], 'required': false},
            ],
            'attachments': resumes,
          },
          options: Options(responseType: ResponseType.stream, receiveTimeout: const Duration(minutes: 5)),
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
      if (event['error'] is String) throw AutofillException(event['error'] as String);
      if (event['id'] is String) yield MapEntry(event['id'] as String, event['answer'] as String?);
    }
  }

  /// The form memory file as it is on the PC now (null: there isn't one yet).
  Future<String?> readMemory() async {
    try {
      final response = await _session(() => _api.dio.get('/files/$memoryPath'));
      return (response.data as Map)['content'] as String?;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  Future<void> writeMemory(String content) async {
    try {
      await _session(() => _api.dio.put('/files/$memoryPath', data: {'content': content}));
    } on DioException catch (e) {
      if (e.response?.statusCode != 404) rethrow;
      await _session(() => _api.dio.post('/files/$memoryPath', data: {'content': content, 'type': 'file'}));
    }
  }

  Future<List<String>> _resumeFiles() async {
    try {
      final response = await _session(() => _api.dio.get('/files/$resumeFolder'));
      final entries = (response.data as Map)['entries'] as List? ?? const [];
      return [for (final entry in entries.cast<Map>()) if (entry['type'] == 'file') entry['path'] as String].take(3).toList();
    } on DioException {
      return const []; // no resume folder: the brain still has the rest of the vault
    }
  }

  /// One request with the stored session; an expired one is renewed once with the device secret.
  Future<Response<dynamic>> _session(Future<Response<dynamic>> Function() request) async {
    await _api.init();
    try {
      return await request();
    } on DioException catch (e) {
      if (e.response?.statusCode != 401) rethrow;
      final name = await _storage.getDeviceName();
      final secret = await _storage.getDeviceSecret();
      final url = await _storage.getServerUrl();
      if (name == null || secret == null || url == null) rethrow;
      await _auth.reconnectDevice(serverUrl: url, deviceName: name, deviceSecret: secret);
      return await request();
    }
  }
}

class AutofillException implements Exception {
  AutofillException(this.message);

  final String message;

  @override
  String toString() => message;
}
