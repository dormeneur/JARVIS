import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/services.dart' show rootBundle;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/core/storage/secure_storage.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

/// A country with its calling code and states/provinces (ISO 3166-2), from the bundled list.
class Country {
  const Country(this.code, this.name, this.dial, this.regions);

  final String code;
  final String name;
  final String dial;

  /// (ISO 3166-2 code, name), sorted by name.
  final List<(String, String)> regions;
}

class Geo {
  Geo(this.countries) : _byCode = {for (final c in countries) c.code: c};

  final List<Country> countries;
  final Map<String, Country> _byCode;

  Country? byCode(String code) => _byCode[code.toUpperCase()];

  String? codeOf(String name) {
    final want = name.trim().toLowerCase();
    for (final c in countries) {
      if (c.name.toLowerCase() == want || c.code.toLowerCase() == want) return c.code;
    }
    return const {'usa': 'US', 'united states of america': 'US', 'uk': 'GB', 'uae': 'AE'}[want];
  }

  String? regionOf(String country, String name) {
    final want = name.trim().toLowerCase();
    for (final (code, n) in byCode(country)?.regions ?? const <(String, String)>[]) {
      if (n.toLowerCase() == want || code.toLowerCase() == want) return code;
    }
    return null;
  }

  Iterable<String> get dials => countries.map((c) => c.dial);

  static Geo parse(String json) => Geo([
        for (final c in (jsonDecode(json) as List).cast<Map>())
          Country(c['code'] as String, c['name'] as String, c['dial'] as String? ?? '', [
            for (final r in (c['regions'] as List).cast<List>()) (r[0] as String, r[1] as String),
          ]),
      ]);
}

final geoProvider = FutureProvider<Geo>((ref) async => Geo.parse(await rootBundle.loadString('assets/geo/countries.json')));

/// What the server found in a resume (already checked against its text, spec §4.3).
class ResumeExtraction {
  const ResumeExtraction(this.profile, this.counts, this.unverified, this.engine);

  final Map<String, dynamic> profile;
  final Map<String, dynamic> counts;
  final List<String> unverified;
  final String engine;
}

class CareerException implements Exception {
  CareerException(this.message);

  final String message;

  @override
  String toString() => message;
}

/// The career profile lives in the vault as Profile/profile.json; resumes in Profile/resume/.
/// Online through jv-api (like the autofill sheet), so the PC sees each change at once.
class CareerRepository {
  CareerRepository(this._api, this._storage);

  static const profilePath = 'Profile/profile.json';
  static const resumeFolder = 'Profile/resume';

  final ApiClient _api;
  final SecureStorage _storage;

  Dio get _dio => _api.dio;

  Future<Map<String, dynamic>?> readProfile() async {
    try {
      final content = ((await _dio.get('/files/$profilePath')).data as Map)['content'] as String?;
      if (content == null || content.trim().isEmpty) return null;
      final decoded = jsonDecode(content);
      return decoded is Map ? Map<String, dynamic>.from(decoded) : null;
    } on DioException catch (e) {
      if (e.response?.statusCode == 404) return null;
      rethrow;
    }
  }

  Future<void> writeProfile(Map<String, dynamic> profile) async {
    profile['updated'] = DateTime.now().toIso8601String();
    final content = const JsonEncoder.withIndent('  ').convert(profile);
    try {
      await _dio.put('/files/$profilePath', data: {'content': content});
    } on DioException catch (e) {
      if (e.response?.statusCode != 404) rethrow;
      await _dio.post('/files/$profilePath', data: {'content': content, 'type': 'file'});
    }
  }

  /// Saves the file in [folder] (created if missing) and returns its vault path.
  Future<String> upload(String folder, String name, List<int> bytes) async {
    final path = '$folder/$name';
    Future<void> send() => _dio.post('/upload/$path',
        data: FormData.fromMap({'file': MultipartFile.fromBytes(bytes, filename: name)}),
        options: Options(sendTimeout: const Duration(minutes: 2)));
    try {
      await send();
    } on DioException catch (e) {
      if (e.response?.statusCode != 404) rethrow;
      await _ensureFolder(folder);
      await send();
    }
    return path;
  }

  Future<void> _ensureFolder(String folder) async {
    var path = '';
    for (final part in folder.split('/')) {
      path = path.isEmpty ? part : '$path/$part';
      try {
        await _dio.post('/files/$path', data: {'type': 'folder'});
      } on DioException catch (e) {
        if (e.response?.statusCode != 409) rethrow; // already there
      }
    }
  }

  Future<List<String>> listFiles(String folder) async {
    try {
      final entries = ((await _dio.get('/files/$folder')).data as Map)['entries'] as List? ?? const [];
      return [for (final e in entries.cast<Map>()) if (e['type'] == 'file') e['path'] as String];
    } on DioException {
      return const [];
    }
  }

  /// Reads the resume (Claude, or the local model when the job engine is off). Can take minutes.
  Future<ResumeExtraction> extract(String path) async {
    try {
      final data = (await _dio.post('/career/resume/extract',
              data: {'path': path}, options: Options(receiveTimeout: const Duration(minutes: 17))))
          .data as Map;
      return ResumeExtraction(
        Map<String, dynamic>.from(data['profile'] as Map),
        Map<String, dynamic>.from(data['counts'] as Map),
        [for (final u in (data['unverified'] as List? ?? const [])) u as String],
        data['engine'] as String? ?? '',
      );
    } on DioException catch (e) {
      final detail = e.response?.data is Map ? (e.response!.data as Map)['detail'] : null;
      throw CareerException(detail is String ? detail : 'Could not read the resume. Is your server running?');
    }
  }

  /// Kept on this phone only (Android Keystore), never sent to the server or any model (spec §8).
  Future<String?> portalPassword() => _storage.getPortalPassword();

  Future<void> setPortalPassword(String value) => _storage.setPortalPassword(value);
}

final careerRepositoryProvider =
    Provider<CareerRepository>((ref) => CareerRepository(ref.watch(apiClientProvider), ref.watch(secureStorageProvider)));
