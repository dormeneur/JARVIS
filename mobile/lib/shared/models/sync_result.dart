/// Summary of a sync operation.
class SyncResult {
  final int pushed;
  final int pulled;
  final int conflicts;
  final List<String> conflictPaths;
  final String? error;

  const SyncResult({
    this.pushed = 0,
    this.pulled = 0,
    this.conflicts = 0,
    this.conflictPaths = const [],
    this.error,
  });

  bool get hasConflicts => conflicts > 0;
  bool get hasError => error != null;
  int get totalChanges => pushed + pulled;

  @override
  String toString() =>
      'SyncResult(pushed: $pushed, pulled: $pulled, conflicts: $conflicts)';
}
