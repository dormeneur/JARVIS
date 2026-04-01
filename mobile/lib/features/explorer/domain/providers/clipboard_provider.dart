import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../state/clipboard_state.dart';

/// Provider for the clipboard state notifier.
///
/// This provider manages the clipboard state for cut/copy/paste operations
/// in the file explorer, including which files are in the clipboard and
/// what operation (cut or copy) should be performed when pasting.
///
/// The clipboard persists across folder navigation until explicitly cleared
/// or a paste-after-cut operation completes.
///
/// Usage:
/// ```dart
/// // Watch the entire clipboard state
/// final clipboardState = ref.watch(clipboardStateProvider);
///
/// // Watch only if clipboard is empty (optimized)
/// final isEmpty = ref.watch(
///   clipboardStateProvider.select((s) => s.isEmpty)
/// );
///
/// // Access the notifier to modify state
/// ref.read(clipboardStateProvider.notifier).cut(fileIds);
/// ref.read(clipboardStateProvider.notifier).copy(fileIds);
/// ref.read(clipboardStateProvider.notifier).clear();
///
/// // Execute paste operation
/// final result = await ref.read(clipboardStateProvider.notifier).paste(
///   targetFolderId,
///   fileService,
/// );
/// ```
final clipboardStateProvider =
    StateNotifierProvider<ClipboardStateNotifier, ClipboardState>((ref) {
  return ClipboardStateNotifier();
});
