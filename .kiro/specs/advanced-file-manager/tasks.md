# Implementation Plan: Advanced File Manager

## Overview

This implementation plan enhances the existing JARVIS mobile app file explorer with multi-select, drag-and-drop, batch operations, and clipboard functionality. The implementation follows Clean Architecture, extending the existing `mobile/lib/features/explorer/` module with new service layers, state management, and UI components. All code will be written in Dart/Flutter, integrating with the existing ExplorerRepository, AppDatabase (SQLite/Drift), and Riverpod state management.

The implementation is organized into 7 phases: data models, service layer, state management, core operations, UI components, drag & drop, and testing. Each task builds incrementally, with checkpoints to ensure stability before proceeding.

## Tasks

- [x] 1. Set up data models and error handling
  - [x] 1.1 Create FileOperationResult and FileOperationError models
    - Create `mobile/lib/features/explorer/domain/models/file_operation_result.dart`
    - Implement FileOperationResult class with successfulIds, errors, isSuccess, isPartialSuccess, isFailure getters
    - Implement FileOperationError class with fileId, fileName, message, and type properties
    - Define FileOperationErrorType enum (validation, conflict, notFound, permission, system)
    - Add message getter to FileOperationResult for user-friendly feedback
    - _Requirements: 20.1, 20.2, 22.1, 22.2, 22.4_

- [x] 2. Implement FileOperationService foundation
  - [x] 2.1 Create FileOperationService class with dependencies
    - Create `mobile/lib/features/explorer/domain/services/file_operation_service.dart`
    - Add constructor with ExplorerRepository and MutationQueue dependencies
    - Define method signatures for moveFile, moveFiles, copyFile, copyFiles, deleteFile, deleteFiles, renameFile
    - Add private helper method signatures: _validateMove, _isCircularMove, _generateUniqueName, _getDescendantIds
    - _Requirements: 4.1, 5.1, 6.1, 7.1, 8.1, 9.1, 10.1_

  - [x] 2.2 Implement path and name validation helpers
    - Implement _isValidPath method checking for empty paths, invalid characters, reserved names
    - Implement _isValidFileName method checking for empty names, invalid characters, reserved names, trailing periods/spaces
    - Add unit tests for edge cases (empty strings, special characters, reserved names)
    - _Requirements: 10.5, 20.3, 20.4_

  - [x] 2.3 Write property test for path and name validation
    - **Property 38: Path Validation Before Operations**
    - **Property 39: Name Validation Before Operations**
    - **Validates: Requirements 20.3, 20.4**
    - Generate random strings with valid/invalid characters
    - Verify validation returns correct results for all inputs
    - Minimum 100 iterations

- [ ] 3. Implement move operations
  - [x] 3.1 Implement circular move detection
    - Implement _isCircularMove method checking if target is the file itself or a descendant
    - Implement _getDescendantIds method with breadth-first traversal
    - Add helper method to ExplorerRepository: getChildrenIds(folderId) and getFolderIds(ids)
    - _Requirements: 15.1, 15.2_


  - [x] 3.2 Write property tests for circular move detection
    - **Property 35: Circular Move Detection - Self**
    - **Property 36: Circular Move Detection - Descendants**
    - **Validates: Requirements 15.1, 15.2**
    - Generate folder hierarchies with varying depths
    - Verify moving folder into itself or descendants returns validation error
    - Verify database remains unchanged after failed move
    - Minimum 100 iterations

  - [x] 3.3 Implement moveFile operation
    - Implement moveFile method with validation (path, circular move, name conflict)
    - Update file path in database using ExplorerRepository
    - Add move mutation to MutationQueue
    - Return FileOperationResult with success or error details
    - Wrap database operations in transaction for atomicity
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5_

  - [x] 3.4 Write property test for single file move
    - **Property 7: Move Updates Path and Syncs**
    - **Property 8: Move Rejects Name Conflicts**
    - **Property 9: Move Validates Paths**
    - **Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.5**
    - Generate random files and target folders
    - Verify successful moves update database and queue mutation
    - Verify name conflicts return errors without database changes
    - Minimum 100 iterations

  - [x] 3.5 Implement moveFiles batch operation
    - Implement moveFiles method processing each file sequentially
    - Skip files with name conflicts, continue with remaining files
    - Collect successful IDs and errors separately
    - Return FileOperationResult with partial success details
    - Ensure all successful moves are synced to mutation queue
    - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5_

  - [ ] 3.6 Write property test for batch move
    - **Property 10: Batch Move Partial Success**
    - **Property 11: Batch Move Syncs All Successes**
    - **Validates: Requirements 5.1, 5.2, 5.3, 5.5, 21.1, 21.5**
    - Generate batches of files with some conflicts
    - Verify non-conflicting files are moved successfully
    - Verify mutation count equals successful move count
    - Minimum 100 iterations

- [ ] 4. Implement copy operations
  - [x] 4.1 Implement unique name generation
    - Implement _generateUniqueName method checking existing names in target folder
    - Extract base name and extension, append numeric suffix (1), (2), etc.
    - Add helper method to ExplorerRepository: getFileNamesInPath(path)
    - Test with various file name formats (with/without extensions)
    - _Requirements: 6.5, 7.2_

  - [x] 4.2 Implement copyFile operation
    - Implement copyFile method with validation
    - Generate new unique ID for copied file
    - Handle name conflicts by calling _generateUniqueName
    - Insert new file entry into database
    - Add create mutation to MutationQueue
    - Return FileOperationResult with success or error details
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5_

  - [ ] 4.3 Write property test for single file copy
    - **Property 12: Copy Creates New Entry with Unique ID**
    - **Property 13: Copy Syncs to Queue**
    - **Property 14: Copy Resolves Name Conflicts**
    - **Validates: Requirements 6.1, 6.2, 6.3, 6.4, 6.5**
    - Generate random files and target folders
    - Verify copied files have different IDs but same content
    - Verify name conflicts are resolved with numeric suffixes
    - Minimum 100 iterations

  - [x] 4.4 Implement copyFiles batch operation
    - Implement copyFiles method processing each file sequentially
    - Resolve all name conflicts with unique suffixes
    - Collect successful IDs and errors separately
    - Return FileOperationResult with partial success details
    - Ensure all successful copies are synced to mutation queue
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5_

  - [ ] 4.5 Write property test for batch copy
    - **Property 15: Batch Copy Resolves All Conflicts**
    - **Property 16: Batch Copy Syncs All Successes**
    - **Validates: Requirements 7.1, 7.2, 7.5, 21.2, 21.5**
    - Generate batches of files with name conflicts
    - Verify all conflicts are resolved with unique names
    - Verify mutation count equals successful copy count
    - Minimum 100 iterations

- [ ] 5. Checkpoint - Verify move and copy operations
  - Ensure all tests pass, ask the user if questions arise.


- [ ] 6. Implement delete operations
  - [x] 6.1 Implement recursive folder deletion
    - Implement _deleteFileRecursive helper method
    - For folders, recursively delete all children before deleting the folder
    - For files, delete directly from database
    - Add delete mutation to MutationQueue for each deleted item
    - Collect successful IDs and errors separately
    - Return FileOperationResult with details
    - _Requirements: 8.1, 8.2, 8.3, 8.4_

  - [ ] 6.2 Write property test for recursive deletion
    - **Property 17: Delete Removes from Database and Syncs**
    - **Property 18: Delete Folder Recursively**
    - **Validates: Requirements 8.1, 8.2, 8.3, 8.4**
    - Generate folder hierarchies with varying depths and children
    - Verify all descendants are deleted when folder is deleted
    - Verify mutation count equals deleted file count
    - Minimum 100 iterations

  - [x] 6.2 Implement deleteFile operation
    - Implement deleteFile method calling _deleteFileRecursive
    - Wrap operation in transaction for atomicity
    - Return FileOperationResult with success or error details
    - _Requirements: 8.1, 8.2, 8.3, 8.4_

  - [x] 6.3 Implement deleteFiles batch operation
    - Implement deleteFiles method processing each file sequentially
    - Handle folders recursively for each item
    - Collect successful IDs and errors separately
    - Return FileOperationResult with partial success details
    - Ensure all successful deletes are synced to mutation queue
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5_

  - [ ] 6.4 Write property test for batch delete
    - **Property 19: Batch Delete Syncs All Successes**
    - **Property 20: Batch Delete Handles Folders Recursively**
    - **Validates: Requirements 9.4, 9.5, 21.3, 21.5**
    - Generate batches including files and folders
    - Verify all descendants of folders are deleted
    - Verify mutation count equals total deleted file count
    - Minimum 100 iterations

- [ ] 7. Implement rename operation
  - [x] 7.1 Implement renameFile operation
    - Implement renameFile method with validation (name format, conflicts)
    - Update file name in database using ExplorerRepository
    - Add update mutation to MutationQueue
    - Return FileOperationResult with success or error details
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5_

  - [ ] 7.2 Write property test for rename
    - **Property 21: Rename Updates Name and Syncs**
    - **Property 22: Rename Rejects Name Conflicts**
    - **Property 23: Rename Validates Names**
    - **Validates: Requirements 10.1, 10.2, 10.3, 10.4, 10.5**
    - Generate random files and new names (valid and invalid)
    - Verify successful renames update database and queue mutation
    - Verify name conflicts and invalid names return errors
    - Minimum 100 iterations

- [ ] 8. Implement selection state management
  - [x] 8.1 Create SelectionState and SelectionStateNotifier
    - Create `mobile/lib/features/explorer/domain/state/selection_state.dart`
    - Define SelectionState class with selectedIds (Set<String>) and isSelectionMode (bool)
    - Implement SelectionStateNotifier extending StateNotifier<SelectionState>
    - Implement toggle(fileId), selectAll(fileIds), clear(), selectRange(orderedFileIds, start, end) methods
    - Add isSelected(fileId) and selectedCount getters
    - _Requirements: 1.1, 1.2, 1.4, 2.1, 2.2, 2.3, 3.1, 3.2, 3.3_

  - [ ] 8.2 Write property tests for selection state
    - **Property 1: Selection Toggle Idempotence**
    - **Property 2: Multi-Select Accumulation**
    - **Property 3: Select All Completeness**
    - **Property 4: Deselect All Clears Selection**
    - **Property 5: Range Selection Inclusivity**
    - **Validates: Requirements 1.1, 1.2, 2.1, 2.2, 2.3, 3.1, 3.3**
    - Generate random file ID lists and selection sequences
    - Verify toggle twice returns to original state
    - Verify selectAll includes all IDs, clear removes all
    - Verify range selection includes all items between indices
    - Minimum 100 iterations per property

  - [x] 8.3 Create Riverpod provider for selection state
    - Create `mobile/lib/features/explorer/domain/providers/selection_provider.dart`
    - Define selectionStateProvider as StateNotifierProvider<SelectionStateNotifier, SelectionState>
    - Export provider for use in UI components
    - _Requirements: 19.1_


- [ ] 9. Implement clipboard state management
  - [x] 9.1 Create ClipboardState and ClipboardStateNotifier
    - Create `mobile/lib/features/explorer/domain/state/clipboard_state.dart`
    - Define ClipboardOperation enum (cut, copy)
    - Define ClipboardState class with fileIds (List<String>) and operation (ClipboardOperation?)
    - Implement ClipboardStateNotifier extending StateNotifier<ClipboardState>
    - Implement cut(fileIds), copy(fileIds), clear() methods
    - Implement paste(targetFolderId, fileService) method that calls appropriate service method
    - Add isEmpty and isNotEmpty getters
    - _Requirements: 11.1, 11.3, 12.1, 12.2, 13.1, 13.2, 13.3, 13.4_

  - [ ] 9.2 Write property tests for clipboard state
    - **Property 24: Cut Stores Files with Move Operation**
    - **Property 25: Cut Replaces Previous Clipboard**
    - **Property 26: Copy Stores Files with Copy Operation**
    - **Property 27: Copy Replaces Previous Clipboard**
    - **Property 28: Clipboard Persists Until Cleared**
    - **Property 29: Paste-Cut Performs Move and Clears**
    - **Property 30: Paste-Copy Performs Copy and Retains**
    - **Validates: Requirements 11.1, 11.3, 11.4, 12.1, 12.2, 12.3, 13.1, 13.2, 13.3, 13.4, 23.3**
    - Generate random file ID lists and clipboard operations
    - Verify cut/copy store correct operation type
    - Verify paste-cut clears clipboard, paste-copy retains
    - Minimum 100 iterations per property

  - [x] 9.3 Create Riverpod provider for clipboard state
    - Create `mobile/lib/features/explorer/domain/providers/clipboard_provider.dart`
    - Define clipboardStateProvider as StateNotifierProvider<ClipboardStateNotifier, ClipboardState>
    - Export provider for use in UI components
    - _Requirements: 19.2_

- [x] 10. Create Riverpod provider for FileOperationService
  - [x] 10.1 Create service provider
    - Create `mobile/lib/features/explorer/domain/providers/file_operation_provider.dart`
    - Define fileOperationServiceProvider as Provider<FileOperationService>
    - Inject ExplorerRepository and MutationQueue dependencies
    - Export provider for use in UI and state notifiers
    - _Requirements: 19.1, 19.2, 19.3_

- [x] 11. Checkpoint - Verify service layer and state management
  - Ensure all tests pass, ask the user if questions arise.

- [ ] 12. Enhance ExplorerScreen with selection mode
  - [ ] 12.1 Update ExplorerScreen to support selection mode
    - Modify `mobile/lib/features/explorer/presentation/screens/explorer_screen.dart`
    - Watch selectionStateProvider and clipboardStateProvider
    - Conditionally render SelectionAppBar when isSelectionMode is true
    - Pass selection state to file tile widgets
    - Add onTap handler that toggles selection when in selection mode
    - Add onLongPress handler that shows context menu
    - _Requirements: 1.1, 1.2, 16.1, 16.2_

  - [ ] 12.2 Implement navigation clears selection
    - Add listener to current folder provider
    - Call selectionState.clear() when folder changes
    - Ensure clipboard state persists across navigation
    - _Requirements: 23.1, 23.2, 23.3_

  - [ ] 12.3 Write property test for navigation clearing selection
    - **Property 41: Navigation Clears Selection**
    - **Validates: Requirements 23.1, 23.2**
    - Generate random selection states and folder navigation sequences
    - Verify selection is cleared after each navigation
    - Minimum 100 iterations

- [ ] 13. Create SelectionAppBar component
  - [ ] 13.1 Implement SelectionAppBar widget
    - Create `mobile/lib/features/explorer/presentation/widgets/selection_app_bar.dart`
    - Display count of selected items in title
    - Add action buttons: Select All, Cut, Copy, Delete, Move
    - Add close button to exit selection mode (calls clear())
    - Watch selectionStateProvider for selected count
    - Trigger appropriate operations via FileOperationService
    - _Requirements: 2.2, 2.3, 11.1, 12.1_

  - [ ] 13.2 Write widget tests for SelectionAppBar
    - Test correct count display
    - Test Select All button calls selectAll()
    - Test Cut button calls clipboard.cut()
    - Test Copy button calls clipboard.copy()
    - Test Delete button shows confirmation dialog
    - Test close button calls clear()


- [ ] 14. Create ContextMenu component
  - [ ] 14.1 Implement ContextMenu widget
    - Create `mobile/lib/features/explorer/presentation/widgets/context_menu.dart`
    - Show platform-adaptive menu (bottom sheet on mobile, popup on desktop)
    - Include options: Rename, Delete, Copy, Cut, Move
    - Conditionally show Paste option if clipboard is not empty
    - Hide Paste option if clipboard is empty
    - Trigger appropriate operations via FileOperationService and clipboard state
    - _Requirements: 16.1, 16.2, 16.3, 16.4, 16.5_

  - [ ] 14.2 Write property tests for context menu visibility
    - **Property 31: Empty Clipboard Hides Paste Option**
    - **Property 32: Non-Empty Clipboard Shows Paste Option**
    - **Validates: Requirements 13.5, 16.4, 16.5**
    - Generate random clipboard states (empty and non-empty)
    - Verify paste option visibility matches clipboard state
    - Minimum 100 iterations

  - [ ] 14.3 Write widget tests for ContextMenu
    - Test menu displays all options for single file
    - Test menu displays batch options for multiple files
    - Test Paste option appears when clipboard has items
    - Test Paste option hidden when clipboard is empty
    - Test each option triggers correct action

- [ ] 15. Update file tile widgets with selection support
  - [ ] 15.1 Add selection checkbox to file tiles
    - Modify `mobile/lib/features/explorer/presentation/widgets/file_tile.dart`
    - Add isSelected parameter to widget
    - Display checkbox when isSelected is true or selection mode is active
    - Update checkbox state based on isSelected
    - Add visual highlight when file is selected
    - _Requirements: 1.3, 2.5_

  - [ ] 15.2 Write property test for selection visual feedback
    - **Property 6: Selection Visual Feedback**
    - **Validates: Requirements 1.3**
    - Generate random selection states
    - Verify visual indicators appear for all selected files
    - Minimum 100 iterations

  - [ ] 15.3 Write widget tests for file tile selection
    - Test checkbox appears in selection mode
    - Test checkbox reflects selection state
    - Test visual highlight appears when selected
    - Test tap toggles selection in selection mode
    - Test long press shows context menu

- [ ] 16. Implement drag and drop functionality
  - [ ] 16.1 Create DraggableFileTile wrapper
    - Create `mobile/lib/features/explorer/presentation/widgets/draggable_file_tile.dart`
    - Wrap file tile with Draggable widget
    - Show semi-transparent feedback during drag
    - Pass file data to drag
    - If multiple files selected, drag all selected files
    - If single file not selected, drag only that file
    - _Requirements: 14.1, 14.2, 14.3_

  - [ ] 16.2 Create DragTargetFolder wrapper
    - Create `mobile/lib/features/explorer/presentation/widgets/drag_target_folder.dart`
    - Wrap folder tiles with DragTarget widget
    - Highlight folder on drag hover
    - Validate drop target (must be folder, not file)
    - On drop, call fileOperationService.moveFiles() with dragged file IDs
    - Show error if drop is invalid
    - _Requirements: 14.1, 14.2, 14.4, 14.5_

  - [ ] 16.3 Write property test for drag-drop move
    - **Property 33: Drag-Drop Performs Move**
    - **Property 34: Invalid Drop Performs No Operation**
    - **Validates: Requirements 14.1, 14.2, 14.5**
    - Generate random file sets and target folders
    - Verify drag-drop moves files to target folder
    - Verify invalid drops don't modify database
    - Minimum 100 iterations

  - [ ] 16.4 Write widget tests for drag and drop
    - Test drag feedback displays during drag
    - Test folder highlights on drag hover
    - Test drop on folder triggers move operation
    - Test drop on file does not trigger operation
    - Test drop on invalid target shows error

- [ ] 17. Checkpoint - Verify UI components and interactions
  - Ensure all tests pass, ask the user if questions arise.


- [ ] 18. Implement visual feedback for operations
  - [ ] 18.1 Add loading indicators for operations
    - Create `mobile/lib/features/explorer/presentation/widgets/operation_loading_indicator.dart`
    - Show loading spinner when file operations are in progress
    - Display operation type (Moving, Copying, Deleting)
    - Show progress for batch operations (e.g., "Moving 5 of 20 files")
    - _Requirements: 24.1, 24.2_

  - [ ] 18.2 Add success and error feedback
    - Create `mobile/lib/features/explorer/presentation/widgets/operation_feedback.dart`
    - Show SnackBar with success message on successful operations
    - Show SnackBar with error message on failed operations
    - Auto-dismiss success messages after 3 seconds
    - Keep error messages until user dismisses
    - For partial success, show detailed message with counts
    - _Requirements: 24.3, 24.4, 24.5, 22.3_

  - [ ] 18.3 Add visual indicator for cut files
    - Update file tile to show dimmed/grayed appearance for cut files
    - Watch clipboardStateProvider to determine if file is cut
    - Remove indicator when clipboard is cleared or paste completes
    - _Requirements: 11.2_

  - [ ] 18.4 Write widget tests for visual feedback
    - Test loading indicator appears during operations
    - Test success message displays and auto-dismisses
    - Test error message displays and persists
    - Test cut files show visual indicator
    - Test partial success shows detailed message

- [ ] 19. Implement folder navigation integration
  - [ ] 19.1 Ensure folder tap navigates correctly
    - Verify existing folder navigation in ExplorerScreen
    - Ensure tapping folder in non-selection mode navigates
    - Ensure tapping folder in selection mode toggles selection (not navigate)
    - _Requirements: 17.1_

  - [ ] 19.2 Write property test for folder navigation
    - **Property 40: Folder Navigation Displays Contents**
    - **Validates: Requirements 17.1**
    - Generate random folder hierarchies
    - Verify tapping folder displays its contents
    - Minimum 100 iterations

  - [ ] 19.3 Add breadcrumb navigation
    - Create `mobile/lib/features/explorer/presentation/widgets/breadcrumb_trail.dart`
    - Display current path as breadcrumb segments
    - Make each segment tappable to navigate to that folder
    - Update breadcrumb when folder changes
    - _Requirements: 17.2, 17.3_

  - [ ] 19.4 Add back navigation support
    - Ensure back button navigates to parent folder
    - Handle root folder case (disable back button)
    - _Requirements: 17.4_

  - [ ] 19.5 Write widget tests for navigation
    - Test breadcrumb displays current path
    - Test breadcrumb segments are tappable
    - Test back button navigates to parent
    - Test back button disabled at root

- [ ] 20. Implement performance optimizations
  - [ ] 20.1 Optimize list rendering
    - Verify ListView.builder is used for file list
    - Add itemExtent hint for consistent tile heights
    - Adjust cacheExtent for smooth scrolling
    - Use const constructors where possible
    - _Requirements: 18.1, 18.2, 2.4_

  - [ ] 20.2 Optimize selection state updates
    - Use ref.watch with select to watch only specific state parts
    - Ensure file list doesn't rebuild when selection changes
    - Ensure only affected tiles rebuild on selection change
    - _Requirements: 2.5, 19.4, 19.5_

  - [ ] 20.3 Optimize batch operations
    - Wrap batch database operations in transactions
    - For batches >100 files, process in chunks of 50
    - Add 10ms delay between chunks to allow UI updates
    - Validate all files in parallel before processing
    - _Requirements: 5.4, 7.4, 9.3, 18.5_

  - [ ] 20.4 Write performance tests
    - Test 1000 file list renders in <1 second
    - Test selection update completes in <16ms
    - Test batch operation on 100 files completes in <5 seconds
    - Test scrolling maintains 60 FPS with 1000 items
    - _Requirements: 17.5, 18.1, 18.2, 18.3, 18.5_

- [ ] 21. Checkpoint - Verify performance and polish
  - Ensure all tests pass, ask the user if questions arise.


- [ ] 22. Write comprehensive property-based tests for error handling
  - [ ] 22.1 Write property test for failed operations don't modify database
    - **Property 42: Failed Operations Don't Modify Database**
    - **Validates: Requirements 20.1**
    - Generate random invalid operations (bad paths, circular moves, etc.)
    - Verify database state is unchanged after failed operations
    - Minimum 100 iterations

  - [ ] 22.2 Write property test for batch operation error reporting
    - **Property 43: Batch Operations Report All Failures**
    - **Validates: Requirements 9.2, 22.2**
    - Generate batches with mix of valid and invalid files
    - Verify all failures are reported with descriptive messages
    - Verify successful operations complete despite failures
    - Minimum 100 iterations

  - [ ] 22.3 Write property test for error object completeness
    - **Property 44: Error Objects Include Descriptive Messages**
    - **Validates: Requirements 15.4, 22.1, 22.4**
    - Generate random error conditions
    - Verify all error objects have non-empty messages and correct types
    - Minimum 100 iterations

  - [ ] 22.4 Write property test for error logging
    - **Property 45: Errors Are Logged with Context**
    - **Validates: Requirements 22.5**
    - Generate random error conditions
    - Verify error logs contain file ID, operation type, and error message
    - Minimum 100 iterations

- [ ] 23. Write property test for move target validation
  - [ ] 23.1 Write property test for move validates target existence
    - **Property 37: Move Validates Target Existence**
    - **Validates: Requirements 15.3**
    - Generate random files and non-existent target folders
    - Verify move returns error without modifying database
    - Minimum 100 iterations

- [ ] 24. Write integration tests for end-to-end workflows
  - [ ] 24.1 Write integration test for cut-paste workflow
    - Test: Select files → Cut → Navigate to folder → Paste
    - Verify files are moved to target folder
    - Verify clipboard is cleared after paste
    - Verify mutations are queued correctly

  - [ ] 24.2 Write integration test for copy-paste workflow
    - Test: Select files → Copy → Paste multiple times
    - Verify multiple copies are created with unique names
    - Verify clipboard is retained after paste
    - Verify mutations are queued correctly

  - [ ] 24.3 Write integration test for drag-drop workflow
    - Test: Drag file to folder → Verify move completed
    - Verify file is moved to target folder
    - Verify mutation is queued correctly

  - [ ] 24.4 Write integration test for recursive folder deletion
    - Test: Delete folder with 100 files
    - Verify all files and subfolders are deleted
    - Verify all mutations are queued correctly

  - [ ] 24.5 Write integration test for batch move with conflicts
    - Test: Batch move 50 files with 10 conflicts
    - Verify 40 files are moved successfully
    - Verify 10 errors are reported with details
    - Verify 40 mutations are queued

- [ ] 25. Add error recovery and retry mechanisms
  - [ ] 25.1 Add retry button for transient errors
    - Update operation feedback widget to show retry button for system errors
    - Store last operation details to enable retry
    - Clear retry state on successful operation
    - _Requirements: 22.1, 22.3_

  - [ ] 25.2 Implement transaction rollback on failure
    - Ensure all database operations use transactions
    - Verify rollback occurs on any operation failure
    - Test rollback with simulated database errors
    - _Requirements: 20.1, 20.5_

  - [ ] 25.3 Write tests for error recovery
    - Test retry button appears for system errors
    - Test retry executes operation again
    - Test transaction rollback on failure
    - Test database consistency after rollback

- [ ] 26. Add logging and debugging support
  - [ ] 26.1 Add comprehensive logging
    - Log all file operations with file IDs, operation type, and result
    - Log validation failures with reason
    - Log batch operation progress (X of Y completed)
    - Use appropriate log levels (info, warning, error)
    - _Requirements: 22.5_

  - [ ] 26.2 Add debug mode for testing
    - Add debug flag to enable verbose logging
    - Add debug overlay showing operation queue status
    - Add debug option to simulate slow operations
    - Add debug option to simulate errors


- [ ] 27. Final integration and polish
  - [ ] 27.1 Verify all requirements are met
    - Review all 24 requirements and verify implementation
    - Test each acceptance criterion manually
    - Ensure all edge cases are handled
    - Verify error messages are user-friendly

  - [ ] 27.2 Verify all correctness properties pass
    - Run all 45 property-based tests
    - Ensure minimum 100 iterations per test
    - Fix any failing properties
    - Document any known limitations

  - [ ] 27.3 Code review and cleanup
    - Remove debug code and console logs
    - Ensure consistent code style
    - Add documentation comments to public APIs
    - Update README with new features

  - [ ] 27.4 Performance validation
    - Test with 1000+ files in a folder
    - Verify 60 FPS scrolling
    - Verify <16ms selection updates
    - Verify <5s batch operations on 100 files
    - Profile and optimize any bottlenecks

  - [ ] 27.5 Accessibility review
    - Ensure all interactive elements have semantic labels
    - Test with screen reader (TalkBack/VoiceOver)
    - Verify keyboard navigation works
    - Ensure sufficient color contrast

- [ ] 28. Final checkpoint - Production readiness
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation at major milestones
- Property tests validate universal correctness properties with minimum 100 iterations
- Unit tests validate specific examples and edge cases
- Integration tests validate end-to-end workflows
- All code is written in Dart/Flutter, extending the existing explorer module
- Performance targets: 1000 files render in <1s, 60 FPS scrolling, <16ms selection updates, <5s batch operations
- The implementation follows Clean Architecture with clear separation between data, domain, and presentation layers
- State management uses Riverpod with separate providers for selection, clipboard, and file operations
- All file operations sync to the mutation queue for server synchronization
- Error handling provides clear, actionable feedback to users
- The feature integrates seamlessly with existing ExplorerRepository and AppDatabase

## Test Summary

**Property-Based Tests (45 properties)**:
- Selection System: Properties 1-6 (6 properties)
- File Operations: Properties 7-23 (17 properties)
- Clipboard: Properties 24-32 (9 properties)
- Drag & Drop: Properties 33-34 (2 properties)
- Validation: Properties 35-39 (5 properties)
- Navigation: Properties 40-41 (2 properties)
- Error Handling: Properties 42-45 (4 properties)

**Unit Tests**:
- FileOperationService: Path validation, circular move detection, move/copy/delete/rename operations
- SelectionStateNotifier: Toggle, select all, clear, range selection
- ClipboardStateNotifier: Cut, copy, paste, clear
- UI Components: SelectionAppBar, ContextMenu, file tiles, drag & drop

**Integration Tests**:
- Cut-paste workflow
- Copy-paste workflow
- Drag-drop workflow
- Recursive folder deletion
- Batch operations with conflicts

**Performance Tests**:
- 1000 file list rendering
- Selection update latency
- Batch operation throughput
- Scrolling frame rate

**Widget Tests**:
- SelectionAppBar actions
- ContextMenu visibility and actions
- File tile selection and visual feedback
- Drag and drop interactions
- Visual feedback for operations
- Navigation components

## Implementation Order Rationale

1. **Data Models First**: Establish error handling and result types before implementing operations
2. **Service Layer**: Build core file operations with validation before adding UI
3. **State Management**: Implement selection and clipboard state before UI components
4. **UI Components**: Build UI after state management is solid
5. **Drag & Drop**: Add advanced interactions after basic UI works
6. **Testing**: Property tests alongside implementation, integration tests at end
7. **Polish**: Performance optimization and accessibility after core functionality works

This order ensures each layer is stable before building on top of it, reducing rework and debugging time.
