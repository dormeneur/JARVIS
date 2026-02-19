import 'package:flutter_test/flutter_test.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';
import 'package:jarvis_mobile/shared/models/sync_result.dart';

void main() {
  group('FileEntry', () {
    test('isDirectory and isFile work correctly', () {
      const dir = FileEntry(
        path: 'Personal',
        name: 'Personal',
        type: 'directory',
        lastModified: '2026-01-01T00:00:00Z',
      );
      expect(dir.isDirectory, true);
      expect(dir.isFile, false);

      const file = FileEntry(
        path: 'readme.md',
        name: 'readme.md',
        type: 'file',
        lastModified: '2026-01-01T00:00:00Z',
      );
      expect(file.isDirectory, false);
      expect(file.isFile, true);
    });

    test('isSynced checks localPath', () {
      const unsynced = FileEntry(
        path: 'a.md',
        name: 'a.md',
        type: 'file',
        lastModified: '2026-01-01T00:00:00Z',
      );
      expect(unsynced.isSynced, false);

      const synced = FileEntry(
        path: 'a.md',
        name: 'a.md',
        type: 'file',
        lastModified: '2026-01-01T00:00:00Z',
        localPath: '/data/mirror/a.md',
      );
      expect(synced.isSynced, true);
    });

    test('toManifestEntry uses correct keys', () {
      const entry = FileEntry(
        path: 'docs/readme.md',
        name: 'readme.md',
        type: 'file',
        contentHash: 'sha256:abc',
        lastModified: '2026-01-15T12:00:00Z',
      );
      final manifest = entry.toManifestEntry();
      expect(manifest['path'], 'docs/readme.md');
      expect(manifest['content_hash'], 'sha256:abc');
      expect(manifest['last_modified'], '2026-01-15T12:00:00Z');
    });

    test('copyWith preserves fields', () {
      const original = FileEntry(
        path: 'a.md',
        name: 'a.md',
        type: 'file',
        lastModified: '2026-01-01T00:00:00Z',
        contentHash: 'sha256:old',
      );
      final updated = original.copyWith(contentHash: 'sha256:new');
      expect(updated.contentHash, 'sha256:new');
      expect(updated.path, 'a.md');
    });
  });

  group('SyncResult', () {
    test('hasConflicts returns true when conflicts > 0', () {
      const result = SyncResult(pushed: 2, pulled: 3, conflicts: 1);
      expect(result.hasConflicts, true);
    });

    test('totalChanges sums pushed and pulled', () {
      const result = SyncResult(pushed: 5, pulled: 3);
      expect(result.totalChanges, 8);
    });

    test('defaults are zero', () {
      const result = SyncResult();
      expect(result.pushed, 0);
      expect(result.pulled, 0);
      expect(result.conflicts, 0);
      expect(result.hasConflicts, false);
      expect(result.hasError, false);
    });
  });
}
