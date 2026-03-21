# JARVIS Sync Engine - Real Device Issues Fix Summary

## Completed Fixes

### ✅ Fix 1: JSON Null Safety (CRITICAL - COMPLETED)

**Problem**: Server may omit `conflicts`, `to_push`, or `to_pull` fields when empty, causing type cast crashes.

**Solution Implemented**:
- Updated `performSync()` Phase 2 manifest parsing:
  ```dart
  final toPush = ((diffResponse['to_push'] as List?) ?? [])
  final toPull = ((diffResponse['to_pull'] as List?) ?? [])
  final conflicts = ((diffResponse['conflicts'] as List?) ?? [])
  ```
- Updated `_pushFile()` response parsing:
  ```dart
  final conflicts = (data['conflicts'] as List?) ?? [];
  final pushed = (data['pushed'] as List?) ?? [];
  ```

**Status**: ✅ COMPLETE - No more type cast crashes possible

**Files Modified**:
- `mobile/lib/features/sync/data/sync_repository.dart`

---

### ✅ Fix 2: Accept Remote for Phase 2 Conflicts (COMPLETED)

**Problem**: "Accept Remote" button was disabled for Phase 2 conflicts (no conflictFilePath).

**Solution Implemented**:
1. Updated `resolveAcceptRemote()` to handle both cases:
   - Phase 1 conflicts: Pull from `conflictFilePath`
   - Phase 2 conflicts: Pull from original `mutation.path`
2. Updated UI to always enable "Accept Remote" button
3. Remote tab already shows appropriate message for Phase 2 conflicts

**Status**: ✅ COMPLETE - Accept Remote now works for all conflict types

**Files Modified**:
- `mobile/lib/features/sync/data/sync_repository.dart`
- `mobile/lib/features/sync/presentation/conflict_detail_screen.dart`

---

## Issue Analysis: resolveKeepLocal State Machine

### Current Behavior

When user taps "Keep Local" on a Phase 2 conflict:

1. `resolveKeepLocal()` is called
2. Mutation baseVersion is updated to current cache serverVersion
3. Mutation status changes from 'failed' to 'pending'
4. User taps Sync again
5. **Conflict reappears**

### Root Cause Analysis

The issue is NOT with `resolveKeepLocal` itself. The issue is with the **duplicate prevention logic** in Phase 2.

Current duplicate prevention:
```dart
final pendingForPath = pendingMutations
    .where((m) => m.path == conflictPath)
    .isNotEmpty;
final failedForPath = failedMutations
    .where((m) => m.path == conflictPath)
    .isNotEmpty;

if (!pendingForPath && !failedForPath) {
  // Create synthetic mutation
}
```

This checks are done at the START of `performSync()`:
```dart
final pendingMutations = await _db.getPendingMutations();
final failedMutations = await _db.getFailedMutations();
```

**The Problem**: These lists are fetched ONCE at the beginning. After Phase 1 processes mutations:
- Successful pushes: mutation is REMOVED from DB
- Failed pushes: mutation status changes to 'failed'

So by the time Phase 2 runs:
- The mutation might have been removed (if push succeeded)
- The mutation might be failed again (if push failed)

But we're still checking against the OLD `pendingMutations` and `failedMutations` lists!

### The Real Issue

After `resolveKeepLocal`:
1. Mutation becomes pending
2. Next sync: Phase 1 pushes successfully
3. Mutation is removed from DB
4. Cache is updated with new serverVersion and contentHash
5. Phase 2 builds manifest - should NOT show conflict anymore

**IF Phase 2 still shows conflict**, it means:
- Either the cache wasn't updated correctly
- Or the local file content doesn't match what was pushed
- Or there's a race condition

### Proposed Solution

The duplicate prevention logic needs to check the CURRENT state, not the state from the beginning of sync:

```dart
// Check for existing mutations AGAIN (not using cached lists)
final existingMutations = await _db.getPendingMutations() + await _db.getFailedMutations();
final hasExistingMutation = existingMutations.any((m) => m.path == conflictPath);

if (!hasExistingMutation) {
  // Create synthetic mutation
}
```

This ensures we don't create duplicates even if Phase 1 modified the mutation queue.

---

## Testing Status

**Current Test Count**: 10/10 passing
- 8 original tests
- 2 new tests (Scenarios E & F)

**Tests Verify**:
- ✅ Scenario A: Manifest conflicts don't accumulate
- ✅ Scenario B: Phase 1 + Phase 2 duplicate prevention
- ✅ Scenario C: Phase 3 doesn't re-push Phase 1 files
- ✅ Scenario D: Push/pull counters accurate
- ✅ Scenario E: Phase 2 creates synthetic mutation row
- ✅ Scenario F: Second sync doesn't create duplicate
- ✅ Counter tests: All pass

---

## Next Steps

### 🔄 Fix 3: Improve Duplicate Prevention Logic (RECOMMENDED)

Update Phase 2 duplicate prevention to query DB again instead of using cached lists:

```dart
// In Phase 2, for each conflict:
for (final conflictPath in conflicts) {
  conflictPaths.add(conflictPath);

  // Query DB again to get current state (not cached from start of sync)
  final currentPending = await _db.getPendingMutations();
  final currentFailed = await _db.getFailedMutations();
  
  final pendingForPath = currentPending.any((m) => m.path == conflictPath);
  final failedForPath = currentFailed.any((m) => m.path == conflictPath);

  if (!pendingForPath && !failedForPath) {
    // Create synthetic mutation
    ...
  }
}
```

**Rationale**: This ensures we check the ACTUAL current state after Phase 1 has modified the mutation queue.

**Risk**: Minimal - just adds DB queries. No logic changes.

**Benefit**: Prevents duplicate synthetic mutations if Phase 1 already handled the conflict.

---

## Manual Testing Checklist

After implementing Fix 3, verify these flows on real device:

### Flow A: Keep Local (Phase 2 Conflict)
1. Create conflict via manifest (edit on laptop, don't sync mobile)
2. Sync mobile → conflict appears
3. Tap conflict → "Keep Local"
4. Sync again
5. ✅ Expected: Conflict gone, file pushed to server
6. ❌ Current: Conflict reappears

### Flow B: Accept Remote (Phase 2 Conflict)
1. Create conflict via manifest
2. Sync mobile → conflict appears
3. Tap conflict → "Accept Remote"
4. ✅ Expected: Local file overwritten, conflict gone
5. Test after Fix 2

### Flow C: Delete From Laptop
1. Delete file on laptop
2. Sync mobile
3. ✅ Expected: No crash, file behavior documented
4. Test after Fix 1 (null safety)

---

## Files Modified

1. `mobile/lib/features/sync/data/sync_repository.dart`
   - JSON null safety fixes
   - resolveAcceptRemote Phase 2 support

2. `mobile/lib/features/sync/presentation/conflict_detail_screen.dart`
   - Enable Accept Remote for all conflicts

3. `mobile/test/sync_state_consistency_test.dart`
   - Updated Scenario A & B expectations
   - Added Scenario E & F tests

---

## Architecture Notes

### Why Tombstones Are Not Implemented

Tombstones would require:
- Backend schema changes (deleted_files table)
- API contract changes (manifest includes tombstones)
- Mobile logic to process tombstones

Current behavior:
- Delete on server → mobile sees "file not in manifest" → treats as "to_push"
- This is incorrect but doesn't crash (after Fix 1)

Future work: Implement proper tombstone support.

### Why resolveKeepLocal Doesn't Push Immediately

Design decision: All pushes go through `performSync()` Phase 1.

Benefits:
- Centralized sync logic
- Consistent error handling
- Offline queue management

Drawback:
- User must tap Sync again after resolution

This is acceptable UX - user is informed "Sync to push."

