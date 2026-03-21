import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:jarvis_mobile/features/auth/presentation/auth_provider.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';

final explorerRepositoryProvider = Provider<ExplorerRepository>((ref) {
  return ExplorerRepository(db: ref.watch(appDatabaseProvider));
});

/// Tracks the current directory path for navigation.
final currentDirectoryProvider = StateProvider<String>((ref) => '');

/// Whether to show hidden files (names starting with '.').
final showHiddenFilesProvider = StateProvider<bool>((ref) => false);

/// Provides the list of entries in the current directory.
final directoryEntriesProvider = FutureProvider<List<FileEntry>>((ref) async {
  final currentDir = ref.watch(currentDirectoryProvider);
  final showHidden = ref.watch(showHiddenFilesProvider);
  final repo = ref.read(explorerRepositoryProvider);
  final entries = await repo.listDirectory(currentDir);

  if (showHidden) return entries;

  // Hide files/folders whose name starts with '.'
  return entries.where((e) => !e.name.startsWith('.')).toList();
});
