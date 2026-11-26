import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/vault_api.dart';
import 'package:jarvis_mobile/features/about_me/domain/knowledge.dart';

final aboutMeRepositoryProvider = Provider<AboutMeRepository>((ref) => AboutMeRepository(ref.watch(vaultApiProvider)));

/// One Profile file on the page.
class ProfileTopic {
  ProfileTopic(this.path, this.facts);

  final String path;
  final List<String> facts;

  String get title => ProfileNotes.title(path);
}

class AboutMeException implements Exception {
  AboutMeException(this.message);

  final String message;

  @override
  String toString() => message;
}

/// "About me" through jv-api: Profile/ (and one folder down, not other features' folders) read and
/// edited line by line. Every change re-reads the file first and replaces only the line it was made
/// on, so a fact added elsewhere meanwhile (a hunch answered on the PC) is never lost.
class AboutMeRepository {
  AboutMeRepository(this._vault);

  final VaultApi _vault;

  Future<List<ProfileTopic>> load() async {
    final entries = <({String path, String name, bool isFolder})>[];
    try {
      for (final item in await _vault.list(ProfileNotes.folder)) {
        entries.add((path: item.path, name: item.name, isFolder: item.isFolder));
        if (item.isFolder && !ProfileNotes.otherFolders.contains(item.name.toLowerCase()) && !item.name.startsWith('.')) {
          for (final inner in await _vault.list(item.path)) {
            entries.add((path: inner.path, name: inner.name, isFolder: inner.isFolder));
          }
        }
      }
      final topics = <ProfileTopic>[];
      for (final path in ProfileNotes.files(entries)) {
        final text = await _vault.read(path);
        if (ProfileNotes.isKnowledge(path, text)) topics.add(ProfileTopic(path, ProfileNotes.facts(text)));
      }
      if (topics.every((t) => t.path != ProfileNotes.traits)) topics.insert(0, ProfileTopic(ProfileNotes.traits, const []));
      return topics;
    } on DioException {
      throw AboutMeException('Your PC can\'t be reached right now. About me needs it (Tailscale, Wi-Fi or USB).');
    }
  }

  /// A fact edited (an empty text deletes it). False: the line changed elsewhere, reload.
  Future<bool> saveFact(String path, String fact, String text) => _change(path, (file) => ProfileNotes.replace(file, fact, text));

  Future<bool> deleteFact(String path, String fact) => _change(path, (file) => ProfileNotes.replace(file, fact, null));

  Future<bool> addFact(String path, String fact) => _change(path, (file) => ProfileNotes.add(file, fact, path));

  /// Files a fact wherever the vault's brain (on the PC, local model) says it belongs. Returns the path.
  Future<String> addAnywhere(String fact) async {
    String path;
    try {
      final response = await _vault.call(() => _vault.dio.post('/ask/profile/place', data: {'fact': fact}, options: Options(receiveTimeout: const Duration(minutes: 2))));
      path = ((response.data as Map)['path'] as String?) ?? ProfileNotes.traits;
    } on DioException catch (e) {
      final detail = (e.response?.data is Map) ? (e.response!.data as Map)['detail'] : null;
      throw AboutMeException(detail is String ? 'Not saved: $detail.' : 'Your PC can\'t be reached, so it wasn\'t saved.');
    }
    await addFact(path, fact);
    return path;
  }

  Future<String> addTopic(String name) async {
    final path = ProfileNotes.topicPath(name);
    if (path == null) throw AboutMeException('Give it a name, like Travel, or people/friends for a folder.');
    if (await _vault.read(path) != null) throw AboutMeException('${ProfileNotes.title(path)} is already there.');
    await _vault.write(path, '# ${ProfileNotes.title(path)}\n\n');
    return path;
  }

  /// Renames (moves) a topic; a bare name keeps it in its folder. Returns the new path.
  Future<String> renameTopic(String path, String name) async {
    final folder = ProfileNotes.folderOf(path);
    final target = ProfileNotes.topicPath(name.contains('/') || folder == null ? name : '${folder.substring(ProfileNotes.folder.length + 1)}/$name');
    if (target == null || target == path) throw AboutMeException('Give it a new name, like Hobbies, or people/friends to move it into a folder.');
    if (await _vault.read(target) != null) throw AboutMeException('There\'s already a ${ProfileNotes.title(target)}.');
    final lines = (await _vault.read(path) ?? '').split('\n');
    if (lines.isNotEmpty && lines.first.startsWith('# ')) lines[0] = '# ${ProfileNotes.title(target)}';
    await _vault.write(target, lines.join('\n'));
    await _vault.delete(path);
    return target;
  }

  Future<void> delete(String path, {bool folder = false}) => _vault.delete(path, folder: folder);

  Future<bool> _change(String path, String? Function(String? file) change) async {
    final updated = change(await _vault.read(path));
    if (updated == null) return false;
    await _vault.write(path, updated);
    return true;
  }
}
