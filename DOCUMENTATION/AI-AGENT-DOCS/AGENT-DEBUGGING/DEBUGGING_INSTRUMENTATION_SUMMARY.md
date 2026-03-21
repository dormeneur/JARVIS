# JARVIS Sync Engine — Comprehensive Debugging Instrumentation

## Summary

Added extensive debug logging throughout the sync pipeline to track the silent overwrite regression where concurrent edits are not detected as conflicts.

---

## Instrumentation Points Added

### 1. **Phase 1 Push - Pre-Push State** (`_pushFile` method)

**Location:** `sync_repository.dart:313-418`

```
[PUSH:PRE] path=$path baseVersion=$baseVersion sqliteServerVersion=$sqliteServerVersion
[PUSH:PAYLOAD] path=$path metadata=$metadata
[PUSH:RESPONSE] raw=$data
```

**Captures:**

- ✅ SQLite cached `serverVersion` BEFORE push attempt
- ✅ `baseVersion` being sent in multipart metadata
- ✅ Raw push response data
- ✅ Verifies if `conflicts` array is present/populated

---

### 2. **Phase 1 Push - Conflict Detection** (`_pushFile` method)

**Location:** `sync_repository.dart:363-380`

```
[PUSH:CONFLICT] path=$path baseVersion=$baseVersion serverVersion=$conflictServerVersion conflictPath:$conflictFilePath
[PUSH:SUCCESS] path=$path baseVersion=$baseVersion newVersion=$newVersion
```

**Captures:**

- ✅ Conflicts detected by server (shows baseVersion vs serverVersion mismatch)
- ✅ Successful push with new version assigned
- ✅ Tracks version progression

---

### 3. **Phase 1 Mutation Processing** (`performSync` Phase 1)

**Location:** `sync_repository.dart:68-125`

```
[SYNC:P1] mutation id=$id path=$path op=$op baseVersion=$baseVersion
[PUSH:PHASE1] Pushing mutation mutationId=$id path=$path baseVersion=$baseVersion
[PUSH:PHASE1:RESULT] mutationId=$id path=$path is_conflict=$is_conflict result=$result
[PUSH:PHASE1:CONFLICT_DETECTED] mutationId=$id path=$path conflictPath=$conflictPath
[PUSH:PHASE1:SUCCESS] mutationId=$id path=$path newVersion=$newVersion
```

**Captures:**

- ✅ Each pending mutation at start of Phase 1
- ✅ Pre-push state with baseVersion
- ✅ Post-push result indicating conflict or success
- ✅ Action taken (mark failed vs. remove mutation)

---

### 4. **Phase 2 Manifest Diff** (`performSync` Phase 2)

**Location:** `sync_repository.dart:168-176`

```
[SYNC:P2] LOCAL MANIFEST — files=$count entries=$entries
[SYNC:P2] MANIFEST DIFF RESPONSE raw=$response
[SYNC] MANIFEST diff — toPush:$count toPull:$count manifestConflicts:$count paths=$paths
```

**Captures:**

- ✅ Local manifest composition (all cached files with their serverVersions)
- ✅ Server's diff response (what it sees vs. what client has)
- ✅ Detected conflicts from manifest analysis

---

### 5. **Phase 2 Synthetic Conflict Creation** (`performSync` Phase 2)

**Location:** `sync_repository.dart:203-248`

```
[SYNC:P2:CONFLICT] Processing manifest-detected conflict path=$path
[SYNC:P2:CONFLICT] pendingForPath=$pending failedForPath=$failed
[SYNC:P2:CONFLICT:CREATE] syntheticId=$id path=$path entry_exists=$bool
    entry.serverVersion=$serverVersion baseVersion_assigned=$baseVersion
```

**Captures:**

- ✅ Each manifest conflict discovered
- ✅ Whether existing mutation rows already cover path
- ✅ baseVersion assigned to synthetic mutation (critical for debugging!)
- ✅ Whether cache entry exists and what version it has

**🔴 CRITICAL:** This shows if Phase 2 is creating synthetic mutations with incorrect baseVersion assignments.

---

### 6. **Database Mutation State Changes**

**Location:** `app_database.dart:228-298`

```
[DB:MARK_CONFLICT] id=$id path=$path status:$old→failed baseVersion:$version retryCount:$old→$new
[DB:UPDATE_BASE_VERSION] id=$id path=$path baseVersion:$old→$new status:$old→pending
[DB:RESET_MUTATION] id=$id path=$path baseVersion:$version status:$old→pending
```

**Captures:**

- ✅ Every mutation status transition
- ✅ baseVersion changes when resolving conflicts
- ✅ Retry count increments
- ✅ Exact state before/after each operation

**🔴 CRITICAL:** Shows if `resetMutation()` is accidentally modifying `baseVersion`, or if state transitions are happening in unexpected orders.

---

### 7. **Conflict Resolution Flow** (`resolveKeepLocal` method)

**Location:** `sync_repository.dart:497-534`

```
[CONFLICT:RESOLVE_KEEP_LOCAL] START mutationId=$id path=$path
    currentBaseVersion=$baseVersion status:$status
[CONFLICT:RESOLVE_KEEP_LOCAL] cacheServerVersion=$version cacheEntry_exists:$bool
[CONFLICT:RESOLVE_KEEP_LOCAL] After updateMutationBaseVersion
[CONFLICT] resolved via Keep Local — mutationId=$id path=$path newBaseVersion=$baseVersion
```

**Captures:**

- ✅ Entry into keep-local resolution
- ✅ Cache state at time of resolution
- ✅ Updated baseVersion value
- ✅ Transition from failed→pending

**🔴 CRITICAL:** Shows if `updateMutationBaseVersion()` is being correctly called and if `resetMutation()` is redundant or corrupting state.

---

## How to Use

### Step 1: Enable Logging Output

Run the Flutter app on your device:

```bash
flutter run --verbose
```

### Step 2: Reproduce the Regression

1. **Start with clean state:** Delete local DB and start fresh
2. **Commit baseline:** Edit file on laptop → sync
3. **Concurrent edit:**
   - Edit same file on mobile (create pending mutation)
   - Edit same file on laptop → sync laptop changes to server
   - DO NOT sync mobile yet
4. **Trigger sync:** Sync mobile app
5. **Observe logs:**
   - Look for `[PUSH:CONFLICT]` to verify Phase 1 detects conflict
   - Look for `[SYNC:P2:CONFLICT]` to verify Phase 2 detects manifest conflict
   - Look for `baseVersion` assignments during creation

### Step 3: Analyze Log Output

**Expected Flow (Correct Behavior):**

```
[SYNC:P1] mutation id=... path=file.txt op=update baseVersion=1
[PUSH:PHASE1] Pushing mutation mutationId=... path=file.txt baseVersion=1
[PUSH:PRE] path=file.txt baseVersion=1 sqliteServerVersion=1
[PUSH:PAYLOAD] path=file.txt metadata={"base_version":1,...}
[PUSH:RESPONSE] raw={"conflicts":[{"path":"file.txt","server_version":2}]}
[PUSH:CONFLICT] path=file.txt baseVersion=1 serverVersion=2 conflictPath=file.txt.conflict
[PUSH:PHASE1:CONFLICT_DETECTED] mutationId=... path=file.txt conflictPath=file.txt.conflict
[DB:MARK_CONFLICT] id=... path=file.txt status:pending→failed
[SYNC:P2:CONFLICT] Processing manifest-detected conflict path=file.txt
[SYNC:P2:CONFLICT:CREATE] syntheticId=... path=file.txt baseVersion_assigned=2
```

**Bad Flow (Silent Overwrite):**

```
[SYNC:P1] mutation id=... path=file.txt op=update baseVersion=1
[PUSH:PHASE1] Pushing mutation mutationId=... path=file.txt baseVersion=1
[PUSH:PRE] path=file.txt baseVersion=1 sqliteServerVersion=1
[PUSH:PAYLOAD] path=file.txt metadata={"base_version":1,...}
[PUSH:RESPONSE] raw={"conflicts":[],"pushed":[...]}  ← NO CONFLICT DETECTED!
[PUSH:PHASE1:SUCCESS] mutationId=... path=file.txt newVersion=3
# File silently overwrites server version 2
[DB:UPDATE_BASE_VERSION] id=... path=file.txt baseVersion:1→3 status:pending→pending
```

### Step 4: Query SQLite State

Before/after sync, run:

```bash
# Connect to device:
adb shell

# Enter SQLite CLI:
sqlite3 /data/data/com.example.jarvis/databases/jarvis.db

# Check cache state BEFORE sync:
SELECT path, server_version FROM file_cache_entries WHERE path='file.txt';

# Check mutation state BEFORE sync:
SELECT id, path, operation, base_version, status FROM mutation_queue
WHERE path='file.txt' ORDER BY timestamp DESC LIMIT 5;
```

**Expected (Correct):**

```
Before sync:
- file.txt in file_cache_entries with server_version=1
- mutation_queue has id=... with baseVersion=1, status='pending'

After sync (after regression):
- No update to file_cache_entries (sync failed due to conflict)
- mutation_queue status='failed' with conflictFilePath set

After sync (correct behavior):
- Conflict detected, mutation_queue status='failed'
```

---

## Critical Logs to Watch

| Log Pattern                                         | Indicates                          | Expected Value                                  |
| --------------------------------------------------- | ---------------------------------- | ----------------------------------------------- |
| `[PUSH:PRE] sqliteServerVersion=$X`                 | Server version in local cache      | Should match actual server version              |
| `[PUSH:PAYLOAD] base_version=$X`                    | What baseVersion is sent to server | Should be \< current server version if conflict |
| `[PUSH:CONFLICT]`                                   | Phase 1 conflict detected          | Should appear if serverVersion > baseVersion    |
| `[SYNC:P2:CONFLICT]`                                | Phase 2 conflict detected          | Should appear for concurrent edits              |
| `[SYNC:P2:CONFLICT:CREATE] baseVersion_assigned=$X` | Synthetic mutation creation        | Should be current serverVersion from cache      |
| `[DB:UPDATE_BASE_VERSION] baseVersion:$A→$B`        | Resolving conflict                 | Should move to latest serverVersion             |

---

## Debugging Checklist

- [ ] Verify `[PUSH:PRE] sqliteServerVersion` matches actual server state
- [ ] Verify `[PUSH:CONFLICT]` appears when base_version < server_version
- [ ] Verify `[PUSH:PAYLOAD]` includes correct baseVersion in metadata
- [ ] Verify Phase 2 `[SYNC:P2:CONFLICT]` detects manifest conflicts
- [ ] Verify synthetic mutation baseVersion = current serverVersion (not baseVersion of other mutation)
- [ ] Verify `[DB:UPDATE_BASE_VERSION]` only logs FROM failed→pending transitions
- [ ] Verify `[DB:RESET_MUTATION]` is NOT being called after `updateMutationBaseVersion()`
- [ ] Verify serverVersion in file_cache_entries is updated after successful push
- [ ] Verify baseVersion in mutation_queue tracks what client THOUGHT server had, not what server ACTUALLY has

---

## Files Modified

1. **mobile/lib/features/sync/data/sync_repository.dart**

   - Added logging to `_pushFile()` showing baseVersion sent/received
   - Added logging to Phase 1 mutation processing showing conflict detection
   - Added logging to Phase 2 manifest conflict creation
   - Added logging to `resolveKeepLocal()` showing state transitions

2. **mobile/lib/core/storage/app_database.dart**
   - Added logging to `markMutationConflict()` showing status transition
   - Added logging to `updateMutationBaseVersion()` showing baseVersion update
   - Added logging to `resetMutation()` showing status transition
   - Added import: `import 'dart:developer' as developer;`

---

## Next Steps

1. ✅ **Instrumentation Complete** — All critical logging points added
2. ⏳ **User Action:** Reproduce regression with logs enabled
3. ⏳ **User Action:** Capture logs and SQLite state before/after
4. ⏳ **Analysis:** Identify which log line stops showing expected pattern
5. ⏳ **Fix:** Apply targeted fix based on evidence
6. ⏳ **Verification:** Run tests to ensure all 82 tests still pass
