import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/core/network/vault_api.dart';
import 'package:uuid/uuid.dart';

final hunchRepositoryProvider = Provider<HunchRepository>((ref) => HunchRepository(ref.watch(vaultApiProvider)));

/// A guess the vault's brain made about the user, and a one-tap way to test it.
class Hunch {
  const Hunch(this.id, this.question, this.options, this.evidence);

  final String id;
  final String question;

  /// Agree, partly, disagree: in the user's voice, written for this guess.
  final List<String> options;
  final List<String> evidence;
}

class HunchBatch {
  const HunchBatch(this.hunch, this.stacked);

  final Hunch? hunch;
  final int stacked;
}

/// Hunches on the phone, the same as the notch on the PC (brain/app/services/hunches.py): one at a
/// time, the stack first; Skip stacks it for the next session (an app run); answers go to
/// Profile/traits.md or not-me.md. Unprompted asks share the PC's daily cap; [next] with onDemand doesn't.
class HunchRepository {
  HunchRepository(this._vault);

  final VaultApi _vault;

  /// This run of the app: a hunch skipped now comes back next time the app starts.
  static final session = 'phone-${const Uuid().v4()}';

  Future<HunchBatch> next({bool onDemand = false}) async {
    final response = await _vault.call(() => _vault.dio.post('/ask/hunches/next', data: {'session': session, 'on_demand': onDemand}));
    final data = response.data as Map;
    final h = data['hunch'];
    Hunch? hunch;
    if (h is Map && h['id'] is String && h['question'] is String) {
      final options = (h['options'] as List? ?? const []).whereType<String>().toList();
      if (options.length == 3) {
        hunch = Hunch(h['id'] as String, h['question'] as String, options, (h['evidence'] as List? ?? const []).whereType<String>().toList());
      }
    }
    return HunchBatch(hunch, (data['stacked'] as num?)?.toInt() ?? 0);
  }

  /// [choice] 0/1/2 (agree/partly/disagree) or the user's own words. Returns the file it went to.
  Future<String?> answer(String id, {int? choice, String? text}) async {
    final response = await _vault.call(() => _vault.dio.post('/ask/hunches/${Uri.encodeComponent(id)}/answer', data: {'choice': choice, 'text': text}));
    return (response.data as Map)['saved_to'] as String?;
  }

  Future<void> skip(String id) async {
    try {
      await _vault.call(() => _vault.dio.post('/ask/hunches/${Uri.encodeComponent(id)}/skip', data: {'session': session}));
    } on DioException {
      // Unanswered, it's asked again next session anyway.
    }
  }
}
