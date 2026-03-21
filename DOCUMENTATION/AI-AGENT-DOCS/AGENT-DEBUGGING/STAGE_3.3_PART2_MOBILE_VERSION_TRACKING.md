# Stage 3.3 Part 2: Mobile Version Tracking Implementation

## Status: ✅ COMPLETE

## Overview
This document describes the implementation of version-based optimistic concurrency control on the mobile side of JARVIS. This completes the version tracking system started in Part 1 (backend), enabling deterministic conflict detection across all devices.

## Objective
Activate version-based conflict detection on mobile by:
1. Storing server version for each file
2. Capturing base version when creating mutations
3. Sending base version in push requests
4. Handling version conflict responses from server
5. Updating server version on successful push

## Implementation Details

### 1. Database Schema Changes

#### FileCacheEntries Table
Added `serverVersion` column to track the server's version of each file:

```dart
IntColumn get serverVersion => integer().withDefault(const Constant(1))();
```

- Default value: 1 (for new files)
- Updated on every pull from server
- Updated on successful push to server

#### MutationQueue Table
Added `baseVersion` column to capture the version the edit was based on:

```dart
IntColumn get baseVersion => integer()();
```

- Required parameter when enqueueing mutations
- Sent to server during push operations
- Used by server to detect conflicts

#### Schema Version
- Incremented from v2 to v3
- Migration logic preserves existing data
- Sets default base_version for existing mutations using file_cache server_version

### 2. Model Updates

#### FileEntry Model
Added `serverVersion` field:

```dart
final int serverVersion;

FileEntry({
  // ... other fields
  this.serverVersion = 1,
});
```

- Included in manifest entries sent to server
- Used when enqueueing mutations
- Preserved across all file operations

### 3. Repository Changes

#### ExplorerRepository
Updated all FileEntry constructors to include `serverVersion`:
- `listDirectory()`: Includes serverVersion from database
- `getEntry()`: Includes serverVersion from database
- `getAllFiles()`: Includes serverVersion from database
- `upsertFile()`: Stores serverVersion in database
- `deleteFile()`: Captures serverVersion as baseVersion for delete mutation

#### SyncRepository
Updated sync operations to handle version tracking:

**Pull Operations (`_pullFile`)**:
- Extracts version from `X-File-Version` response header
- Stores version in `serverVersion` column
- Falls back to version 1 if header missing

**Push Operations (`_pushFile`)**:
- Accepts `baseVersion` parameter
- Includes `base_version` in metadata JSON
- Extracts new version from push response
- Returns version in result map

**Mutation Queue Processing**:
- Passes `baseVersion` from mutation to `_pushFile`
- Updates `serverVersion` on successful push
- Preserves `baseVersion` in failed mutations for retry

**Manifest Diff Push**:
- Uses current `entry.serverVersion` as baseVersion
- Updates `serverVersion` after successful push

### 4. Editor Changes

#### EditorScreen
Updated save operation to enqueue mutations with baseVersion:

```dart
await db.enqueueMutation(
  id: 'edit-${DateTime.now().millisecondsSinceEpoch}-${_entry!.path.hashCode}',
  path: _entry!.path,
  operation: 'update',
  timestamp: mtime,
  baseVersion: _entry!.serverVersion,  // ← Captures current version
);
```

### 5. Testing

#### Database Tests (database_test.dart)
- Updated all `enqueueMutation` calls to include `baseVersion`
- Added assertion for `baseVersion` in mutation queue test
- All 44 existing tests pass

#### Version Tracking Tests (version_tracking_test.dart)
Created comprehensive test suite with 14 tests covering:

**Schema Tests**:
- Default serverVersion value (1)
- Custom serverVersion storage
- baseVersion column in mutation queue
- serverVersion updates on upsert

**Migration Tests**:
- v2 to v3 migration adds columns correctly
- Existing data preserved

**Workflow Tests**:
- Mutation captures current serverVersion as baseVersion
- Multiple edits track different baseVersions
- Delete mutations capture serverVersion
- Successful sync updates serverVersion

**Conflict Detection Tests**:
- Stale baseVersion scenario
- Up-to-date baseVersion scenario

**Edge Cases**:
- New files start at version 1
- Version can increment beyond typical ranges
- Failed mutations retain baseVersion
- Reset mutations preserve baseVersion

**Test Results**: All 58 tests pass (44 existing + 14 new)

## Version-Based Conflict Detection Flow

### Scenario 1: No Conflict (Happy Path)
1. Device A pulls `test.md` at version 3
2. Device A edits file locally
3. Device A enqueues mutation with `baseVersion=3`
4. Device A pushes to server with `base_version=3`
5. Server checks: `base_version (3) == current_version (3)` ✅
6. Server accepts push, increments version to 4
7. Device A updates local `serverVersion=4`

### Scenario 2: Conflict Detected
1. Device A pulls `test.md` at version 3
2. Device B pulls `test.md` at version 3
3. Device B edits and pushes first (version → 4)
4. Device A edits locally (still thinks version is 3)
5. Device A enqueues mutation with `baseVersion=3`
6. Device A pushes to server with `base_version=3`
7. Server checks: `base_version (3) != current_version (4)` ❌
8. Server creates conflict file: `test_conflict_<timestamp>.md`
9. Server returns conflict in response
10. Device A marks mutation as failed
11. User must resolve conflict manually

## Files Modified

### Core Files
- `mobile/lib/core/storage/app_database.dart` - Schema v3, migration logic
- `mobile/lib/shared/models/file_entry.dart` - Added serverVersion field

### Repository Files
- `mobile/lib/features/explorer/data/explorer_repository.dart` - serverVersion in all FileEntry constructors
- `mobile/lib/features/sync/data/sync_repository.dart` - Version tracking in push/pull

### UI Files
- `mobile/lib/features/editor/presentation/editor_screen.dart` - Enqueue mutations with baseVersion

### Test Files
- `mobile/test/database_test.dart` - Updated for baseVersion parameter
- `mobile/test/version_tracking_test.dart` - New comprehensive test suite

## Integration with Backend

The mobile implementation integrates seamlessly with the backend version tracking system:

### API Contract
- **Manifest entries** include `version` field
- **Push metadata** includes `base_version` field
- **Push response** includes `version` in pushed entries
- **Pull response** includes `X-File-Version` header
- **Conflict response** includes conflict entries in `conflicts` array

### Backend Validation
The backend (implemented in Part 1) performs the actual conflict detection:
- Compares `base_version` from client with current server version
- Creates conflict files when versions mismatch
- Returns new version on successful push
- Maintains version history in SQLite database

## Validation Checklist

✅ Database schema updated with version columns  
✅ Migration logic preserves existing data  
✅ FileEntry model includes serverVersion  
✅ ExplorerRepository includes serverVersion in all operations  
✅ SyncRepository sends base_version in push requests  
✅ SyncRepository stores server_version from pull responses  
✅ SyncRepository updates server_version on successful push  
✅ EditorScreen enqueues mutations with baseVersion  
✅ All existing tests pass (44 tests)  
✅ New version tracking tests pass (14 tests)  
✅ Drift generated code regenerated  
✅ No compilation errors  

## Next Steps

With version tracking complete on both backend and mobile:

1. **Manual Testing**: Test conflict detection with two devices
   - Pull same file on both devices
   - Edit on device A, sync successfully
   - Edit on device B (stale version), sync triggers conflict
   - Verify conflict file created on server
   - Verify mutation marked as failed on device B

2. **Stage 3.4**: Implement conflict resolution UI
   - Show conflict files to user
   - Allow user to choose version or merge manually
   - Retry failed mutations after resolution

3. **Stage 3.5**: Background sync
   - Automatic sync on app resume
   - Periodic sync while app active
   - Sync on network reconnection

4. **Stage 4**: AI integration
   - AI-powered conflict resolution suggestions
   - Semantic merge assistance

## Technical Notes

### Why Version-Based Instead of Timestamp-Based?

Timestamp-based conflict detection is fundamentally flawed:
- Clock drift between devices causes false conflicts
- Cannot detect true concurrent edits (only clock differences)
- Silent data loss when clocks are synchronized
- No deterministic ordering of operations

Version-based conflict detection:
- Deterministic: version mismatch = conflict
- Clock-independent: works regardless of device time
- Proven approach: used by Git, databases, CRDTs
- Enables future CRDT implementation

### Version Increment Strategy

- Server increments version on every successful push
- Client stores version from server responses
- Client sends base_version with every push
- Server rejects push if base_version != current_version
- Conflict files get their own version tracking

### Migration Safety

The v2→v3 migration is safe because:
- Adds columns with default values (no data loss)
- Sets base_version for existing mutations intelligently
- Preserves all existing file cache and mutation data
- Tested with in-memory database

## Performance Considerations

- Version tracking adds minimal overhead:
  - 1 integer column per file (4 bytes)
  - 1 integer column per mutation (4 bytes)
  - No additional queries or network requests
  - Version comparison is O(1)

## Conclusion

Stage 3.3 Part 2 successfully implements version-based optimistic concurrency control on the mobile side. Combined with Part 1 (backend), JARVIS now has deterministic conflict detection that prevents silent data loss and enables future features like automatic conflict resolution and CRDT-based merging.

The implementation is minimal, well-tested, and follows the architectural principles established in the backend. All 58 tests pass, and the system is ready for manual validation and integration testing.
