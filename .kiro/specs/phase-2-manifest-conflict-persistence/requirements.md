# Requirements Document

## Introduction

This bugfix addresses a critical system invariant violation in the JARVIS mobile sync engine. Phase 2 (_postManifest) of the sync process currently detects manifest conflicts and adds them to the conflictPaths set, but fails to create corresponding MutationQueue rows in the database. This violates the fundamental system invariant: "Every conflict must correspond to a MutationQueue row."

The absence of mutation rows causes multiple downstream issues:
- UI cannot display remote snapshots for manifest conflicts
- Conflicts reappear on every sync cycle
- The resolveKeepLocal operation cannot function
- Users cannot resolve manifest conflicts through the standard resolution flow

This bugfix implements a minimal surgical fix to Phase 2 that creates synthetic mutation rows for manifest conflicts while preserving all existing behavior and maintaining the 82 passing tests.

## Glossary

- **Sync_Engine**: The mobile synchronization system that coordinates local and remote file state
- **Phase_2**: The _postManifest phase of sync that processes server manifest entries
- **Manifest_Conflict**: A conflict detected when server manifest shows a newer version than local state
- **MutationQueue**: The database table storing pending and failed file operations
- **Mutation_Row**: A single record in the MutationQueue table
- **Synthetic_Mutation**: A mutation row created by the system (not from user action) to represent a conflict state
- **Conflict_Path**: The file path where a conflict has been detected
- **Base_Version**: The server version number at the time of conflict detection
- **Conflict_Resolution_Flow**: The UI and logic that allows users to resolve conflicts

## Requirements

### Requirement 1: Synthetic Mutation Creation

**User Story:** As a sync engine, I want to create mutation rows for Phase 2 manifest conflicts, so that the system invariant "Every conflict must correspond to a MutationQueue row" is maintained.

#### Acceptance Criteria

1. WHEN Phase 2 detects a manifest conflict, THE Sync_Engine SHALL create a synthetic mutation row for that conflict path
2. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the mutation ID to 'manifest-conflict-${timestamp}-${path.hashCode}'
3. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the path field to the conflict path
4. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the operation field to 'update'
5. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the timestamp to current UTC ISO8601 format
6. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the retryCount to 0
7. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the status to 'failed'
8. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the baseVersion to entry.serverVersion
9. WHEN creating a synthetic mutation row, THE Sync_Engine SHALL set the conflictFilePath to null

### Requirement 2: Duplicate Prevention

**User Story:** As a sync engine, I want to avoid creating duplicate mutation rows for the same manifest conflict, so that subsequent syncs do not pollute the database with redundant entries.

#### Acceptance Criteria

1. WHEN Phase 2 encounters a manifest conflict path, THE Sync_Engine SHALL check if a mutation row already exists for that path
2. IF a mutation row already exists for the conflict path, THEN THE Sync_Engine SHALL skip creating a new mutation row
3. WHEN checking for existing mutation rows, THE Sync_Engine SHALL query by path field only

### Requirement 3: Backward Compatibility

**User Story:** As a developer, I want the bugfix to preserve all existing behavior, so that the 82 passing tests continue to pass and no regressions are introduced.

#### Acceptance Criteria

1. THE Sync_Engine SHALL NOT modify backend systems
2. THE Sync_Engine SHALL NOT modify API contracts
3. THE Sync_Engine SHALL NOT change database schema
4. THE Sync_Engine SHALL NOT modify Phase 1 logic
5. THE Sync_Engine SHALL NOT modify processedPaths logic
6. THE Sync_Engine SHALL NOT modify conflict resolution logic
7. WHEN all existing tests are run, THE Sync_Engine SHALL pass all 82 tests

### Requirement 4: Test Coverage

**User Story:** As a developer, I want comprehensive test coverage for the bugfix, so that the fix is verified and future regressions are prevented.

#### Acceptance Criteria

1. THE Sync_Engine SHALL include a test for Scenario E: Phase 2 manifest conflict creates a failed mutation row
2. THE Sync_Engine SHALL include a test for Scenario F: Second sync with same manifest conflict does NOT create duplicate mutation rows
3. WHEN Scenario E test runs, THE Sync_Engine SHALL verify that a mutation row exists with status 'failed' and correct field values
4. WHEN Scenario F test runs, THE Sync_Engine SHALL verify that only one mutation row exists for the conflict path after two sync cycles

### Requirement 5: Conflict Resolution Enablement

**User Story:** As a user, I want manifest conflicts to be resolvable through the UI, so that I can choose to keep local or remote versions and continue working.

#### Acceptance Criteria

1. WHEN a manifest conflict mutation row exists, THE Conflict_Resolution_Flow SHALL be able to display the conflict in the UI
2. WHEN a user invokes resolveKeepLocal on a manifest conflict, THE Conflict_Resolution_Flow SHALL be able to process the resolution
3. WHEN a manifest conflict is resolved, THE Sync_Engine SHALL NOT recreate the conflict on subsequent syncs
