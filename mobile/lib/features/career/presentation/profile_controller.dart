import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/career/data/career_repository.dart';
import 'package:jarvis_mobile/features/career/domain/career_profile.dart';

/// Holds Profile/profile.json while the career screens edit it. Every edit calls [changed]; the
/// file is saved shortly after the last one (and on [flush]), so typing never waits on the network.
class ProfileController extends ChangeNotifier {
  ProfileController(this._repo);

  final CareerRepository _repo;
  Json? profile;
  Object? loadError;
  String? saveError;
  Timer? _timer;
  bool _disposed = false;

  bool get loading => profile == null && loadError == null;

  Future<void> load() async {
    loadError = null;
    try {
      profile = normalizeProfile(await _repo.readProfile());
    } catch (e) {
      loadError = e;
    }
    if (!_disposed) notifyListeners();
  }

  void changed() {
    if (!_disposed) notifyListeners();
    _timer?.cancel();
    _timer = Timer(const Duration(milliseconds: 800), flush);
  }

  Future<void> flush() async {
    _timer?.cancel();
    final p = profile;
    if (p == null) return;
    try {
      await _repo.writeProfile(p);
      saveError = null;
    } catch (_) {
      saveError = 'Not saved yet: your server is unreachable. Changes are kept while this screen is open.';
    }
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    if (_timer?.isActive ?? false) unawaited(flush());
    super.dispose();
  }
}

final profileControllerProvider = ChangeNotifierProvider.autoDispose<ProfileController>(
    (ref) => ProfileController(ref.watch(careerRepositoryProvider))..load());
