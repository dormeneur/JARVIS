# Bug Report: Two Remaining Sync Issues After Latest Fixes

## Status
Both root causes identified by code audit. Not yet fixed. For AI coding agent implementation.

## Do NOT Break (Currently Working)
- Conflict detection when both devices edit the same file simultaneously ✅
- Conflict UI showing correct Local and Remote content ✅
- Mobile-side edits pushing correctly to server ✅
- New files only on server being pulled correctly ✅
- New files only on mobile being pushed correctly ✅

---

## Issue 1: Laptop Changes Are Not Pulled — Mobile Overwrites Server Instead

### Symptom
When only the laptop edits a file and the mobile syncs, the mobile's old version overwrites
the server. The laptop changes are silently destroyed. No conflict screen appears.

### Exact Root Cause

The problem is in **`buildLocalManifest()`** in `sync_repository.dart`.

```dart
Future<List<Map<String, dynamic>>> buildLocalManifest() async {
  final files = await _explorerRepo.getAllFiles();
  return files
      .where((f) => f.isFile && f.contentHash != null)
      .map((f) => f.toManifestEntry())
      .toList();
}
```

And `toManifestEntry()` in `file_entry.dart`:

```dart
Map<String, dynamic> toManifestEntry() => {
  'path': path,
  'content_hash': contentHash ?? '',   // ← mobile's LOCAL file content hash
  'last_modified': lastModified,
  'version': serverVersion,            // ← last server version mobile knows about
};
```

The mobile sends its **local file content hash** (`contentHash`) in the manifest.
This hash is the hash of the file content stored in the local mirror on the phone.

Now look at `diff_manifests()` in `sync.py` (services):

```python
if client_version is not None and server_version is not None:
    if client_version == server_version:
        # Versions equal but content differs → only mobile changed → push
        to_push.append(path)

    elif client_version < server_version:
        if tracked_hash is not None and client_hash == tracked_hash:
            # Mobile content matches what server last recorded → pull
            to_pull.append(path)
        else:
            # Both sides changed → conflict
            conflicts.append(path)
```

The pull condition is: `client_hash == tracked_hash`

**`tracked_hash`** = the hash the server recorded in `file_versions.db` when it last wrote
the file. **`client_hash`** = the hash of the mobile's local file content.

### Why the Pull Never Triggers

After a successful pull, `_pullFile()` in `sync_repository.dart` stores the pulled content
in the local mirror file and updates SQLite with the new `contentHash`. This is correct.

**BUT** — `tracked_hash` on the server is updated by `version_tracker.increment_version()`,
which is called from `vault.py` `update_file()`. The hash stored there is computed from the
content at the time `update_file()` was called.

When the mobile later builds its manifest, it sends `content_hash` = the hash of whatever
is in its local mirror file. If the mobile file hasn't changed since the last pull, this
hash equals the hash of the content that was pulled — which was the server's content at that
time — which is exactly what `tracked_hash` would have been at that point.

**The scenario that breaks:**

1. File at version 2. Server content = `"hello"`, `tracked_hash` = hash(`"hello"`).
   Mobile local file = `"hello"`, mobile `serverVersion` = 2.

2. Laptop edits file via `update_file()`. Server content = `"hello world"`,
   version → 3, `tracked_hash` = hash(`"hello world"`).

3. Mobile syncs. Sends manifest: `{ content_hash: hash("hello"), version: 2 }`.

4. Server `diff_manifests()` sees:
   - `client_version=2` < `server_version=3` → server is ahead
   - `client_hash` = hash(`"hello"`) vs `tracked_hash` = hash(`"hello world"`)
   - `client_hash != tracked_hash` → goes to **conflict** ❌

   It should go to **pull** because the mobile hasn't changed its content. But because
   `tracked_hash` was updated to the NEW server content by `update_file()`, the mobile's
   old content no longer matches it.

### The Correct Logic

The mobile is **unchanged** when its content matches what the **server had at the version
the mobile last synced at** — not what the server has now.

`tracked_hash` reflects the CURRENT server content hash, not the old one. So comparing
`client_hash == tracked_hash` can never be true when the server has been updated, because
`tracked_hash` was just changed to the new content.

**The correct comparison is:**

> Mobile is unchanged if `client_version == the version the mobile's content corresponds to`

The simplest correct check: **if `client_version < server_version` and the mobile's
content hash matches what the server recorded at `client_version`** — but we don't store
historical hashes per version.

**The correct fix is simpler**: compare `client_hash` against the **server's current actual
file content hash** (`server_hash` / `content_hash` in the manifest entry), NOT
`tracked_hash`. If the mobile's local content hash is different from the server's current
content, AND it's also different from what it was at the mobile's known version — that's a
conflict. But if the mobile's content IS the same as what the server had at the mobile's
last-known version... we need historical tracking for that.

**Simplest correct fix that works**: In `diff_manifests()`, when `client_version < server_version`,
instead of checking `client_hash == tracked_hash`, check if there is a **pending mutation
for this path on the client**. If there's no pending mutation, the mobile hasn't changed
the file → pull. The server already knows this because the mobile sends its manifest without
a pending mutation path.

But `diff_manifests()` doesn't receive mutation info. The cleanest server-side fix:

**Store the previous version's hash in `file_versions.db`** so we can check what the mobile
last had. Add a `prev_hash` column to the `file_versions` table. When `increment_version()`
is called, save the old `last_hash` as `prev_hash` before updating.

Then in `diff_manifests()`:

```python
elif client_version < server_version:
    # Get what the server content was at the mobile's last known version
    prev_hash = server_entry.get("prev_hash")  # hash before latest server edit
    if prev_hash is not None and client_hash == prev_hash:
        # Mobile content matches server's previous version → mobile untouched → pull
        to_pull.append(path)
    elif client_hash == server_entry["content_hash"]:
        # Mobile somehow already has the new content → nothing to do
        pass  # (shouldn't reach here since we already checked hash equality above)
    else:
        # Mobile content differs from both old and new server content → conflict
        conflicts.append(path)
```

### Files to Modify for Issue 1

| File | Change |
|------|--------|
| `server/app/services/version_tracker.py` | Add `prev_hash TEXT` column to `file_versions` table. In `increment_version()`, save old `last_hash` as `prev_hash` before updating. Add `get_prev_hash(path) -> Optional[str]` method. Add migration for existing rows (set `prev_hash = last_hash` as safe default). |
| `server/app/services/sync.py` `build_server_manifest()` | Include `prev_hash` from version tracker in each manifest entry: `"prev_hash": version_tracker.get_prev_hash(relative)` |
| `server/app/services/sync.py` `diff_manifests()` | In `client_version < server_version` branch: compare `client_hash == prev_hash` (not `tracked_hash`) to determine pull vs conflict |

---

## Issue 2: Phantom Conflict Shown After Sync With No Actual Conflicts

### Symptom
After syncing when only the laptop changed (should be a simple pull), the sync summary
dialog shows a conflict warning. Tapping "View Conflicts" opens the conflict list screen
which is empty. This is a phantom conflict — there's no real conflict, but the UI shows one.

### Exact Root Cause

In `sync_repository.dart`, Phase 2 conflict handling:

```dart
for (final cp in conflicts) {
  if (conflictedPaths.contains(cp)) continue;

  conflictPaths.add(cp);   // ← adds to conflictPaths

  // Double-check DB for any existing row for this path.
  final currentPending = await _db.getPendingMutations();
  final currentFailed = await _db.getFailedMutations();
  final hasRow = currentPending.any((m) => m.path == cp) ||
                 currentFailed.any((m) => m.path == cp);

  if (hasRow) continue;   // ← skips creating a DB row

  // ... creates synthetic mutation row ...
}
```

When `diff_manifests()` incorrectly returns a path in `conflicts` (Issue 1 above — it
should be `to_pull` but goes to `conflicts` instead), the mobile:

1. Adds the path to `conflictPaths` **before** the `hasRow` check
2. Checks if there's already a mutation row for it
3. If Issue 1 is fixed properly and the path goes to `to_pull` instead of `conflicts`,
   this whole block is skipped — problem solved

**But there's a secondary issue**: even when `hasRow = true` and we skip creating a new
DB row, we already added `cp` to `conflictPaths` on line 1 above. So `SyncResult` returns
`conflicts: conflictPaths.length > 0` even though no new conflict row was persisted.

This causes the sync summary dialog to show a conflict badge, but since no new row was
created in the DB, the conflict list screen is empty.

### The Fix for Issue 2

Move `conflictPaths.add(cp)` to AFTER the `hasRow` check and after the synthetic row is
successfully created. Only count it as a conflict if we actually persisted a row:

```dart
for (final cp in conflicts) {
  if (conflictedPaths.contains(cp)) continue;

  // Double-check DB for any existing row for this path.
  final currentPending = await _db.getPendingMutations();
  final currentFailed = await _db.getFailedMutations();
  final hasRow = currentPending.any((m) => m.path == cp) ||
                 currentFailed.any((m) => m.path == cp);

  if (hasRow) {
    // Row already exists — count it (it's a real conflict from Phase 1)
    conflictPaths.add(cp);
    continue;
  }

  // Read local content for snapshot
  String localSnapshot = '';
  final entry = await _explorerRepo.getEntry(cp);
  if (entry?.localPath != null) {
    final f = File(entry!.localPath!);
    if (f.existsSync()) localSnapshot = await f.readAsString();
  }

  final baseVer = entry?.serverVersion ?? 1;
  final syntheticId = 'conflict-${DateTime.now().millisecondsSinceEpoch}'
      '-${cp.hashCode.abs()}';

  await _db.enqueueMutation(
    id: syntheticId,
    path: cp,
    operation: 'update',
    timestamp: DateTime.now().toUtc().toIso8601String(),
    baseVersion: baseVer,
  );
  await _db.markMutationAsConflict(syntheticId, localSnapshot, baseVer);
  conflictedPaths.add(cp);
  conflictPaths.add(cp);   // ← only add AFTER successfully persisting the row
}
```

### Files to Modify for Issue 2

| File | Change |
|------|--------|
| `mobile/lib/features/sync/data/sync_repository.dart` | In Phase 2 conflict loop: move `conflictPaths.add(cp)` to after the row is persisted, not before the `hasRow` check. When `hasRow` is true, add to `conflictPaths` there (it's a real persisted conflict from Phase 1). |

---

## Implementation Order

Fix Issue 2 first (it's a 3-line change in one file, zero risk of regression).
Fix Issue 1 second (requires schema change + server logic, more involved).

After both fixes, the complete verification matrix should be:

| Scenario | Expected |
|----------|----------|
| Only laptop edits → mobile syncs | Silent pull, no conflict dialog |
| Only mobile edits → mobile syncs | Silent push, no conflict dialog |
| Both edit same file → mobile syncs | Conflict dialog with both versions |
| No changes → mobile syncs | No dialog, no badge |
| New file on laptop → mobile syncs | Silent pull |
| New file on mobile → mobile syncs | Silent push |
