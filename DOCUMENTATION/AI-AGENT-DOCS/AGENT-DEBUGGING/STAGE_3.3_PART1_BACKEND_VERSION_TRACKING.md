# Stage 3.3 Part 1 — Backend Version Tracking

## Completion Status: ✅ COMPLETE

**Date:** February 19, 2026  
**Objective:** Implement version-based conflict detection on the backend to replace flawed timestamp-based approach.

---

## Changes Made

### 1. Version Tracker Module ✅

**File: `server/app/services/version_tracker.py`** (NEW)
- Created `VersionTracker` class for managing file version numbers
- SQLite database at `/JARVIS/system/file_versions.db`
- Schema: `file_versions(path TEXT PRIMARY KEY, version INTEGER, last_hash TEXT)`
- Methods:
  - `get_version(path)` - Get current version for a file
  - `get_version_and_hash(path)` - Get version and hash together
  - `create_version(path, hash)` - Create initial version (returns 1)
  - `increment_version(path, hash)` - Increment version (returns new version)
  - `delete_version(path)` - Remove version tracking for deleted file
  - `upsert_version(path, version, hash)` - Insert or update version entry

### 2. Sync Models Updated ✅

**File: `server/app/models/sync_models.py`**
- `ManifestEntry`: Added `version: int | None` field
- `PushMetadata`: Added `base_version: int | None` field (client's known server version)
- `PushResultEntry`: Added `version: int | None` field (new server version after push)

### 3. Sync Service Updated ✅

**File: `server/app/services/sync.py`**
- `build_server_manifest()`:
  - Now includes version number for each file
  - Auto-creates version 1 for files without version tracking
- `diff_manifests()`:
  - **Primary logic**: Version-based conflict detection
    - If `client_base_version != server_version` → CONFLICT
    - If versions match but hashes differ → PUSH (client has new changes)
  - **Fallback logic**: Timestamp-based (when version info missing)
    - Uses tolerance window for backward compatibility
- `push_file()`:
  - Now accepts `base_version` parameter
  - Checks version mismatch before accepting writes
  - Returns tuple: `(path, is_conflict, new_version)`
  - Increments version on successful write
  - Creates conflict file with version tracking if mismatch detected

### 4. Sync Router Updated ✅

**File: `server/app/routers/sync.py`**
- `/sync/manifest`: Includes version in client entries passed to diff_manifests
- `/sync/push`: 
  - Passes `base_version` to push_file
  - Returns new version in accepted response

### 5. Files Router Updated ✅

**File: `server/app/routers/files.py`**
- `DELETE /files/{path}`: Now cleans up version tracking on file deletion

---

## Test Coverage

### New Tests Added

**File: `server/tests/test_version_tracker.py`** (NEW)
- 14 comprehensive tests for VersionTracker class
- All tests pass (Windows SQLite cleanup warnings are non-critical)

**File: `server/tests/test_sync.py`** (UPDATED)
- Added 4 new version-based conflict detection tests:
  - `test_version_match_different_hash_to_push` - Same version, different hash → push
  - `test_version_mismatch_conflict` - Version mismatch → conflict
  - `test_version_mismatch_even_with_newer_timestamp` - Version wins over timestamp
  - `test_fallback_to_timestamp_when_no_version` - Backward compatibility
- Added 7 new push_file version tests:
  - `test_push_new_file_creates_version` - New files get version 1
  - `test_push_with_matching_version_increments` - Matching version increments
  - `test_push_with_version_mismatch_creates_conflict` - Version mismatch creates conflict
  - `test_push_version_mismatch_even_with_newer_timestamp` - Version check ignores timestamp
  - `test_push_identical_hash_returns_current_version` - No-op returns current version
  - `test_push_fallback_to_timestamp_when_no_base_version` - Fallback for old clients
  - `test_push_conflict_file_gets_version_tracking` - Conflict files tracked

### Test Results
- ✅ All 49 sync tests passing
- ✅ All 160 backend tests passing
- ✅ 14 version tracker tests passing (with Windows cleanup warnings)
- ✅ Maintained backward compatibility with timestamp-based logic

---

## Key Improvements

### 1. True Concurrent Edit Detection
- **Before:** Timestamp comparison could miss concurrent edits
- **After:** Version mismatch reliably detects concurrent edits
- **Example:**
  ```
  Device A: Edits file at 10:00:00 (version 1 → 2)
  Device B: Edits file at 10:00:05 (has version 1, server has 2)
  Result: CONFLICT detected (version mismatch), not silent data loss
  ```

### 2. Timestamp Independence
- Version-based detection works regardless of clock drift
- Timestamps kept for display/ordering only
- No more false conflicts from millisecond differences

### 3. Backward Compatibility
- Falls back to timestamp-based logic when version info missing
- Allows gradual migration of clients
- Old clients continue to work (with timestamp logic)

### 4. Deterministic Behavior
- Version numbers are monotonically increasing
- No ambiguity in conflict detection
- Clear audit trail of file changes

---

## Architecture

### Version Lifecycle

```
1. File Created
   └─> Version 1 assigned

2. File Updated (with matching base_version)
   └─> Version incremented (2, 3, 4, ...)

3. File Updated (with mismatched base_version)
   └─> CONFLICT → Create conflict file with version 1

4. File Deleted
   └─> Version tracking removed
```

### Conflict Detection Flow

```
Client Push Request:
  path: "notes.md"
  base_version: 3
  content_hash: "sha256:abc..."

Server Check:
  current_version = get_version("notes.md")  # Returns 5
  
  if base_version (3) != current_version (5):
    → CONFLICT: Create notes_conflict_20260219T120000Z.md
  else:
    → ACCEPT: Write file, increment to version 6
```

---

## Database Schema

```sql
CREATE TABLE file_versions (
    path TEXT PRIMARY KEY,
    version INTEGER NOT NULL,
    last_hash TEXT NOT NULL
);
```

**Location:** `/JARVIS/system/file_versions.db`

---

## Breaking Changes

**None.** All changes are backward compatible:
- Old clients without version support use timestamp fallback
- New clients with version support get better conflict detection
- Server handles both gracefully

---

## Known Issues

### Windows SQLite Cleanup
- Test teardown shows SQLite file locking warnings on Windows
- Tests themselves pass correctly
- Non-critical: Database files are cleaned up eventually
- Does not affect production behavior

---

## Next Steps (Part 2 - Mobile Version Tracking)

1. **Mobile Database Schema Update**
   - Add `server_version INTEGER` to `FileCacheEntries` table
   - Add `base_version INTEGER` to `MutationQueue` table
   - Schema migration v2 → v3

2. **Mobile Sync Repository Update**
   - Store server_version when pulling files
   - Include base_version in mutation queue entries
   - Send base_version in push requests
   - Handle version conflict responses

3. **Mobile Testing**
   - Add tests for version storage
   - Add tests for version-based conflict detection
   - Verify conflict handling in UI

---

## Conclusion

Part 1 successfully implements version-based conflict detection on the backend. The system now uses optimistic concurrency control (like Git, databases, CRDTs) instead of flawed timestamp comparison. This eliminates silent data loss from concurrent edits while maintaining backward compatibility with timestamp-based clients.

**All 160 backend tests passing. Ready for Part 2 (Mobile Version Tracking).**
