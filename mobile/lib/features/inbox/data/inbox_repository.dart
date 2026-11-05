import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/api_client.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';

/// Something JARVIS wants to save or do, waiting for the user's yes (a "proposal" on the server).
class Proposal {
  const Proposal({
    required this.id,
    required this.title,
    required this.detail,
    required this.source,
    required this.status,
    this.result = '',
  });

  final String id;
  final String title;

  /// Markdown: exactly what would be written.
  final String detail;
  final String source;
  final String status;
  final String result;

  factory Proposal.fromJson(Map<String, dynamic> j) => Proposal(
        id: j['id'] as String? ?? '',
        title: j['title'] as String? ?? '',
        detail: j['detail'] as String? ?? '',
        source: j['source'] as String? ?? '',
        status: j['status'] as String? ?? 'pending',
        result: j['result'] as String? ?? '',
      );

  /// A short sentence for the snackbar after a decision.
  String get outcome => switch (status) {
        'done' => 'Saved',
        'rejected' => 'Dismissed',
        'stale' => 'The file changed since, so it was skipped',
        'expired' => 'That was too old to apply',
        'failed' => 'Failed: ${result.isEmpty ? 'unknown error' : result}',
        _ => status,
      };
}

/// Online-only: the proposals live on the server, so there is no local table.
class InboxRepository {
  InboxRepository(this._api);

  final ApiClient _api;

  Dio get _dio => _api.dio;

  Future<List<Proposal>> list() async {
    final data = (await _dio.get('/ask/proposals')).data as Map;
    return (data['proposals'] as List)
        .map((p) => Proposal.fromJson(Map<String, dynamic>.from(p as Map)))
        .toList();
  }

  Future<int> count() async => ((await _dio.get('/ask/proposals/count')).data as Map)['pending'] as int? ?? 0;

  Future<Proposal> decide(String id, bool approve) async {
    final data = (await _dio.post('/ask/proposals/$id/decision', data: {'approve': approve})).data as Map;
    return Proposal.fromJson(Map<String, dynamic>.from(data));
  }
}

final inboxRepositoryProvider =
    Provider<InboxRepository>((ref) => InboxRepository(ref.watch(apiClientProvider)));

final inboxProvider = FutureProvider.autoDispose<List<Proposal>>(
    (ref) => ref.watch(inboxRepositoryProvider).list());

/// The home tile's badge. Any failure (offline, older server) is just "nothing waiting": it must
/// never get in the way of opening the app.
final inboxCountProvider = FutureProvider.autoDispose<int>((ref) async {
  try {
    return await ref.watch(inboxRepositoryProvider).count();
  } catch (_) {
    return 0;
  }
});
