# Implementation Plan: Phase 2 Manifest Conflict Persistence

## Overview

This implementation plan addresses the bugfix for Phase 2 manifest conflict persistence. The code has already been implemented in `sync_repository.dart` (lines 147-186), so the focus is on verifying the implementation and adding comprehensive test coverage.

The implementation adds synthetic mutation row creation in Phase 2 when manifest conflicts are detected, ensuring the system invariant "Every conflict must correspond to a MutationQueue row" is maintained.

## Tasks

- [x] 1. Verify existing implementation in sync_repository.dart
  - Review Phase 2 conflict persistence logic (lines 147-186)
  - Confirm synthetic mutation ID format matches specification
  - Confirm duplicate prevention logic is correct
  - Confirm all required fields are set correctly
  - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2.1, 2.2_

- [x] 2. Add unit test for Scenario E: Phase 2 manifest conflict creates mutation row
  - [x] 2.1 Create test case in sync_repository_test.dart
    - Set up mock server to return manifest conflict
    - Execute performSync()
    - Verify mutation row exists with correct path
    - Verify mutation status is 'failed'
    - Verify mutation operation is 'update'
    - Verify mutation ID matches pattern 'manifest-conflict-*'
    - Verify conflictFilePath is null
    - _Requirements: 1.1, 1.3, 1.7, 4.1, 4.3_

- [x] 3. Add unit test for Scenario F: Duplicate prevention
  - [x] 3.1 Create test case in sync_repository_test.dart
    - Set up mock server to return same manifest conflict twice
    - Execute performSync() first time
    - Count mutation rows for conflict path
    - Execute performSync() second time
    - Verify mutation row count unchanged
    - Verify no duplicate rows created
    - _Requirements: 2.1, 2.2, 4.2, 4.4_

- [ ] 4. Add unit test for resolveKeepLocal with synthetic mutation
  - [ ] 4.1 Create test case in sync_repository_test.dart
    - Create synthetic mutation row manually
    - Call resolveKeepLocal() with synthetic mutation ID
    - Verify mutation baseVersion is updated
    - Verify mutation status changes to 'pending'
    - Verify no errors occur
    - _Requirements: 5.2_

- [ ]* 5. Add property test for Property 1: Mutation row creation
  - [ ]* 5.1 Implement property-based test
    - **Property 1: Manifest Conflict Creates Mutation Row**
    - **Validates: Requirements 1.1, 1.3**
    - Generate random conflict paths (100+ iterations)
    - Mock server to return conflicts
    - Execute performSync()
    - Verify mutation row exists for each conflict path
    - Tag: "Feature: phase-2-manifest-conflict-persistence, Property 1: Manifest conflict creates mutation row"

- [ ]* 6. Add property test for Property 2: Field values correctness
  - [ ]* 6.1 Implement property-based test
    - **Property 2: Synthetic Mutation Has Correct Field Values**
    - **Validates: Requirements 1.2, 1.4, 1.5, 1.6, 1.8, 1.9**
    - Generate random conflict paths and server versions (100+ iterations)
    - Mock server to return conflicts
    - Execute performSync()
    - Verify mutation ID matches pattern
    - Verify operation='update', retryCount=0, conflictFilePath=null
    - Verify timestamp is valid ISO8601 and recent
    - Verify baseVersion matches cache entry
    - Tag: "Feature: phase-2-manifest-conflict-persistence, Property 2: Synthetic mutation has correct field values"

- [ ]* 7. Add property test for Property 3: Status is failed
  - [ ]* 7.1 Implement property-based test
    - **Property 3: Synthetic Mutation Status Is Failed**
    - **Validates: Requirements 1.7**
    - Generate random conflict paths (100+ iterations)
    - Mock server to return conflicts
    - Execute performSync()
    - Verify mutation status='failed' (not 'pending')
    - Tag: "Feature: phase-2-manifest-conflict-persistence, Property 3: Synthetic mutation status is failed"

- [ ]* 8. Add property test for Property 4: Duplicate prevention
  - [ ]* 8.1 Implement property-based test
    - **Property 4: Duplicate Prevention**
    - **Validates: Requirements 2.1, 2.2, 5.3**
    - Generate random conflict paths (100+ iterations)
    - Mock server to return same conflicts multiple times
    - Execute performSync() multiple times
    - Count mutation rows for each conflict path
    - Verify count remains 1 across all sync cycles
    - Tag: "Feature: phase-2-manifest-conflict-persistence, Property 4: Duplicate prevention"

- [x] 9. Run regression test suite
  - [x] 9.1 Execute all existing tests
    - Run full test suite: `flutter test mobile/test/features/sync/data/sync_repository_test.dart`
    - Verify all 82 existing tests pass
    - Verify no test failures or regressions
    - _Requirements: 3.7_

- [x] 10. Checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional property-based tests and can be skipped for faster verification
- The implementation already exists in the codebase (lines 147-186 of sync_repository.dart)
- Focus is on test coverage to verify correctness and prevent regressions
- Each property test should run minimum 100 iterations
- All 82 existing tests must continue to pass (regression requirement)
- Unit tests (tasks 2-4) are required; property tests (tasks 5-8) are optional but recommended
