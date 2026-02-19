import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

final explorerRepositoryProvider = Provider<ExplorerRepository>((ref) {
  return ExplorerRepository(db: ref.watch(appDatabaseProvider));
});

/// Tracks the current directory path for navigation.
final currentDirectoryProvider = StateProvider<String>((ref) => '');

/// Provides the list of entries in the current directory.
final directoryEntriesProvider = FutureProvider<List<FileEntry>>((ref) async {
  final currentDir = ref.watch(currentDirectoryProvider);
  final repo = ref.read(explorerRepositoryProvider);
  return repo.listDirectory(currentDir);
});
