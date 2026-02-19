# Design Document: Phase 2 Manifest Conflict Persistence

## Overview

This bugfix addresses a critical system invariant violation in the JARVIS mobile sync engine where Phase 2 manifest conflicts were not persisted to the MutationQueue table. The fix implements a minimal surgical change that creates synthetic mutation rows for manifest conflicts detected during the _postManifest phase.

The solution maintains backward compatibility by:
- Not modifying backend systems, API contracts, or database schema
- Preserving all existing Phase 1, Phase 3, and conflict resolution logic
- Ensuring all 82 existing tests continue to pass
- Only adding logic to Phase 2 to create synthetic mutation rows

## Architecture

The sync engine operates in four phases:

1. **Phase 1**: Process pending and failed mutations from the queue
2. **Phase 2**: Build local manifest and send to server for diff analysis
3. **Phase 3**: Push remaining files identified by manifest diff
4. **Phase 4**: Pull files from server

The bugfix targets Phase 2 specifically, adding logic after the manifest diff response is received to persist conflict information.

### System Invariant

**Core Invariant**: Every conflict must correspond to a MutationQueue row.

This invariant enables:
- UI display of conflicts
- User-initiated conflict resolution flows (keep local, accept remote, manual edit)
- Prevention of duplicate conflict detection on subsequent syncs

## Components and Interfaces

### Modified Component: SyncRepository.performSync()

The `performSync()` method in `sync_repository.dart` contains the four-phase sync logic. The bugfix adds a conflict persistence block within Phase 2.

### Phase 2 Enhancement

After receiving the manifest diff response containing `conflicts` array, the system will:

1. Iterate through each conflict path
2. Check if a mutation row already exists for that path
3. If no mutation row exists, create a synthetic mutation row
4. Mark the synthetic mutation as failed immediately

### Synthetic Mutation Row Structure

```dart
{
  id: 'manifest-conflict-${timestamp}-${path.hashCode}',
  path: conflictPath,
  operation: 'update',
  timestamp: DateTime.now().toUtc().toIso8601String(),
  retryCount: 0,
  status: 'failed',
  baseVersion: entry.serverVersion,
  conflictFilePath: null
}
```

### Database Operations

The implementation uses existing database methods:
- `_db.enqueueMutation()` - Creates the mutation row
- `_db.markMutationFailed()` - Sets status to 'failed'
- `_db.getPendingMutations()` - Used for duplicate detection
- `_db.getFailedMutations()` - Used for duplicate detection
- `_explorerRepo.getEntry()` - Retrieves baseVersion from cache

### Duplicate Prevention Logic

Before creating a synthetic mutation:

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

This ensures that:
- If a mutation already exists from Phase 1, we don't create a duplicate
- If a synthetic mutation was created on a previous sync, we don't create another
- Only truly new manifest conflicts get mutation rows

## Data Models

### MutationQueue Row

Existing schema (no changes):

```dart
class MutationQueue {
  String id;              // Primary key
  String path;            // File path
  String operation;       // 'create', 'update', 'delete'
  String timestamp;       // ISO8601 UTC
  int retryCount;         // Number of retry attempts
  String status;          // 'pending' or 'failed'
  int baseVersion;        // Server version at time of mutation
  String? conflictFilePath; // Path to conflict file (if any)
}
```

### Synthetic Mutation ID Format

The ID uses a deterministic format to ensure uniqueness:

```
manifest-conflict-${millisecondsSinceEpoch}-${absoluteHashCode}
```

Components:
- `manifest-conflict-` prefix identifies synthetic mutations
- `${millisecondsSinceEpoch}` provides temporal uniqueness
- `${absoluteHashCode}` provides path-based uniqueness

Example: `manifest-conflict-1704067200000-123456789`

## Correctness Properties

*A property is a characteristic or behavior that should hold true across all valid executions of a system—essentially, a formal statement about what the system should do. Properties serve as the bridge between human-readable specifications and machine-verifiable correctness guarantees.*


### Property 1: Manifest Conflict Creates Mutation Row

*For any* manifest conflict path returned by the server in Phase 2, after sync completes, a mutation row should exist in the database with that path.

**Validates: Requirements 1.1, 1.3**

### Property 2: Synthetic Mutation Has Correct Field Values

*For any* synthetic mutation row created for a manifest conflict, the mutation should have:
- ID matching pattern 'manifest-conflict-${timestamp}-${hashCode}'
- operation field set to 'update'
- timestamp in valid UTC ISO8601 format
- retryCount set to 0
- baseVersion matching the cache entry's serverVersion
- conflictFilePath set to null

**Validates: Requirements 1.2, 1.4, 1.5, 1.6, 1.8, 1.9**

### Property 3: Synthetic Mutation Status Is Failed

*For any* synthetic mutation row created for a manifest conflict, the mutation status should be 'failed' (not 'pending').

**Validates: Requirements 1.7**

### Property 4: Duplicate Prevention

*For any* manifest conflict path, if a mutation row already exists for that path (either pending or failed), running sync again with the same conflict should not create an additional mutation row. The total count of mutation rows for that path should remain unchanged.

**Validates: Requirements 2.1, 2.2, 5.3**

## Error Handling

### Existing Mutation Row

When a manifest conflict is detected but a mutation row already exists:
- Log the skip action with path information
- Do not create a duplicate row
- Continue processing other conflicts

### Missing Cache Entry

When creating a synthetic mutation but the cache entry doesn't exist:
- Default baseVersion to 1
- Continue with mutation creation
- Log the default value usage

### Database Errors

Database operations during synthetic mutation creation use existing error handling:
- Errors during `enqueueMutation()` propagate up
- Errors during `markMutationFailed()` propagate up
- Sync fails and returns error to caller

## Testing Strategy

### Dual Testing Approach

This bugfix requires both unit tests and property-based tests:

**Unit Tests**: Verify specific scenarios and edge cases
- Scenario E: Phase 2 manifest conflict creates a failed mutation row
- Scenario F: Second sync with same manifest conflict does NOT create duplicate rows
- Scenario: resolveKeepLocal works with synthetic mutations
- Scenario: Missing cache entry defaults baseVersion to 1
- Regression: All 82 existing tests continue to pass

**Property Tests**: Verify universal properties across all inputs
- Property 1: Manifest conflict creates mutation row
- Property 2: Synthetic mutation has correct field values
- Property 3: Synthetic mutation status is failed
- Property 4: Duplicate prevention

### Property-Based Testing Configuration

**Library**: Use the `test` package with custom property-based testing helpers (or `dart_check` if available)

**Configuration**:
- Minimum 100 iterations per property test
- Each test tagged with: `Feature: phase-2-manifest-conflict-persistence, Property {N}: {property_text}`
- Each correctness property implemented by a SINGLE property-based test

**Test Data Generation**:
- Generate random conflict paths
- Generate random server versions
- Generate random timestamps
- Simulate multiple sync cycles

### Test Organization

Tests should be added to the existing sync repository test file:
- `mobile/test/features/sync/data/sync_repository_test.dart`

New test cases:
1. Unit test for Scenario E (manifest conflict persistence)
2. Unit test for Scenario F (duplicate prevention)
3. Unit test for resolveKeepLocal with synthetic mutation
4. Property test for Property 1 (mutation row creation)
5. Property test for Property 2 (field values)
6. Property test for Property 3 (status=failed)
7. Property test for Property 4 (duplicate prevention)

### Regression Testing

All 82 existing tests must continue to pass:
- Phase 1 mutation processing tests
- Phase 3 push tests
- Phase 4 pull tests
- Conflict resolution tests
- Edge case tests

The bugfix should not affect any existing test behavior since it only adds logic to Phase 2 for a previously unhandled case.
