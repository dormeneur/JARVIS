# Requirements Document: Advanced File Manager

## Introduction

This document specifies requirements for enhancing the JARVIS mobile app's file management capabilities. The enhancement adds multi-select, drag-and-drop, batch operations, and a clipboard system to the existing explorer feature. The system must maintain data integrity, sync with the backend server, and handle 1000+ files without performance degradation.

## Glossary

- **File_Manager**: The enhanced file management system within the JARVIS mobile app
- **File_Entry**: A data model representing a file or folder with id, name, path, isFolder, and updatedAt properties
- **Selection_System**: The subsystem managing multi-select state using Set<String> of file IDs
- **File_Service**: The service layer providing move(), copy(), delete(), and rename() operations
- **Explorer_Repository**: The existing repository interface for file operations and data access
- **App_Database**: The local SQLite database caching file metadata
- **Mutation_Queue**: The existing sync system that queues local changes for server synchronization
- **Clipboard_State**: Internal state tracking cut/copied files and operation type
- **Target_Folder**: The destination folder for move, copy, or paste operations
- **Selected_Items**: The set of File_Entry objects currently selected by the user
- **Batch_Operation**: A file operation applied to multiple Selected_Items simultaneously

## Requirements

### Requirement 1: Single File Selection

**User Story:** As a user, I want to select a single file by tapping it, so that I can perform operations on that file.

#### Acceptance Criteria

1. WHEN the user taps a File_Entry, THE Selection_System SHALL add that file's ID to the selected set
2. WHEN the user taps a File_Entry that is already selected, THE Selection_System SHALL remove that file's ID from the selected set
3. WHEN a File_Entry is selected, THE File_Manager SHALL display a visual indicator on that item
4. THE Selection_System SHALL maintain selection state using a Set<String> of file IDs

### Requirement 2: Multi-Select Operations

**User Story:** As a user, I want to select multiple files, so that I can perform batch operations efficiently.

#### Acceptance Criteria

1. WHEN the user taps multiple File_Entry items in sequence, THE Selection_System SHALL add each tapped item to the selected set
2. WHEN the user activates "Select All", THE Selection_System SHALL add all visible File_Entry IDs to the selected set
3. WHEN the user activates "Deselect All", THE Selection_System SHALL clear the selected set
4. THE Selection_System SHALL support selecting at least 1000 File_Entry items without performance degradation
5. FOR ALL selected File_Entry items, THE File_Manager SHALL display visual indicators within 16ms of selection change

### Requirement 3: Range Selection

**User Story:** As a user, I want to select a range of files, so that I can quickly select multiple consecutive items.

#### Acceptance Criteria

1. WHEN the user performs a range-select gesture on two File_Entry items, THE Selection_System SHALL select all items between and including those two items
2. WHEN no items are currently selected and the user performs range-select, THE Selection_System SHALL select from the first item to the target item
3. THE Selection_System SHALL determine range boundaries based on the current sort order of File_Entry items

### Requirement 4: Move Single File

**User Story:** As a developer, I want to move a file to a different folder, so that I can reorganize my file structure.

#### Acceptance Criteria

1. WHEN the File_Service receives a move request with a valid File_Entry and Target_Folder, THE File_Service SHALL update the File_Entry path to the Target_Folder path
2. WHEN the File_Service completes a move operation, THE File_Service SHALL update the App_Database with the new path
3. WHEN the File_Service completes a move operation, THE File_Service SHALL add the operation to the Mutation_Queue
4. IF the Target_Folder contains a File_Entry with the same name, THEN THE File_Service SHALL return an error and not perform the move
5. IF the Target_Folder path is invalid, THEN THE File_Service SHALL return an error and not perform the move

### Requirement 5: Move Multiple Files

**User Story:** As a user, I want to move multiple selected files to a folder, so that I can reorganize files efficiently.

#### Acceptance Criteria

1. WHEN the File_Service receives a batch move request with Selected_Items and a Target_Folder, THE File_Service SHALL move each File_Entry in Selected_Items to the Target_Folder
2. IF any File_Entry in Selected_Items has the same name as an existing file in Target_Folder, THEN THE File_Service SHALL skip that file and continue with remaining files
3. WHEN the File_Service completes a batch move, THE File_Service SHALL return a result indicating which files succeeded and which failed
4. THE File_Service SHALL process batch move operations for at least 100 files within 5 seconds
5. FOR ALL successfully moved files, THE File_Service SHALL update the App_Database and add operations to the Mutation_Queue

### Requirement 6: Copy Single File

**User Story:** As a user, I want to copy a file to another folder, so that I can duplicate it without losing the original.

#### Acceptance Criteria

1. WHEN the File_Service receives a copy request with a valid File_Entry and Target_Folder, THE File_Service SHALL create a new File_Entry in the Target_Folder with the same content
2. WHEN the File_Service creates a copied File_Entry, THE File_Service SHALL generate a new unique ID for the copy
3. WHEN the File_Service completes a copy operation, THE File_Service SHALL insert the new File_Entry into the App_Database
4. WHEN the File_Service completes a copy operation, THE File_Service SHALL add the operation to the Mutation_Queue
5. IF the Target_Folder contains a File_Entry with the same name, THEN THE File_Service SHALL append a numeric suffix to the copied file name

### Requirement 7: Copy Multiple Files

**User Story:** As a user, I want to copy multiple selected files to a folder, so that I can duplicate multiple files efficiently.

#### Acceptance Criteria

1. WHEN the File_Service receives a batch copy request with Selected_Items and a Target_Folder, THE File_Service SHALL copy each File_Entry in Selected_Items to the Target_Folder
2. FOR ALL File_Entry items with name conflicts, THE File_Service SHALL append numeric suffixes to create unique names
3. WHEN the File_Service completes a batch copy, THE File_Service SHALL return a result indicating which files succeeded and which failed
4. THE File_Service SHALL process batch copy operations for at least 100 files within 5 seconds
5. FOR ALL successfully copied files, THE File_Service SHALL insert new entries into the App_Database and add operations to the Mutation_Queue

### Requirement 8: Delete Single File

**User Story:** As a user, I want to delete a file, so that I can remove unwanted files from my system.

#### Acceptance Criteria

1. WHEN the File_Service receives a delete request with a valid File_Entry, THE File_Service SHALL remove that File_Entry from the App_Database
2. WHEN the File_Service completes a delete operation, THE File_Service SHALL add the operation to the Mutation_Queue
3. IF the File_Entry is a folder, THE File_Service SHALL delete all contained File_Entry items recursively
4. WHEN the File_Service deletes a folder, THE File_Service SHALL delete all child items before deleting the folder itself

### Requirement 9: Delete Multiple Files

**User Story:** As a user, I want to delete multiple selected files, so that I can remove multiple unwanted files efficiently.

#### Acceptance Criteria

1. WHEN the File_Service receives a batch delete request with Selected_Items, THE File_Service SHALL delete each File_Entry in Selected_Items
2. WHEN the File_Service completes a batch delete, THE File_Service SHALL return a result indicating which files succeeded and which failed
3. THE File_Service SHALL process batch delete operations for at least 100 files within 5 seconds
4. FOR ALL successfully deleted files, THE File_Service SHALL remove entries from the App_Database and add operations to the Mutation_Queue
5. IF Selected_Items contains folders, THE File_Service SHALL delete all contained items recursively before deleting each folder

### Requirement 10: Rename File

**User Story:** As a user, I want to rename a file, so that I can give it a more descriptive name.

#### Acceptance Criteria

1. WHEN the File_Service receives a rename request with a valid File_Entry and new name, THE File_Service SHALL update the File_Entry name property
2. WHEN the File_Service completes a rename operation, THE File_Service SHALL update the App_Database with the new name
3. WHEN the File_Service completes a rename operation, THE File_Service SHALL add the operation to the Mutation_Queue
4. IF the parent folder contains a File_Entry with the new name, THEN THE File_Service SHALL return an error and not perform the rename
5. IF the new name contains invalid characters, THEN THE File_Service SHALL return an error and not perform the rename

### Requirement 11: Clipboard Cut Operation

**User Story:** As a user, I want to cut files to the clipboard, so that I can move them to another location later.

#### Acceptance Criteria

1. WHEN the user activates cut with Selected_Items, THE Clipboard_State SHALL store the Selected_Items and set operation type to "move"
2. WHEN the Clipboard_State stores cut items, THE File_Manager SHALL display a visual indicator on those items
3. WHEN the user cuts new items, THE Clipboard_State SHALL replace previously stored items
4. THE Clipboard_State SHALL maintain clipboard contents until a paste operation completes or the user cuts/copies new items

### Requirement 12: Clipboard Copy Operation

**User Story:** As a user, I want to copy files to the clipboard, so that I can duplicate them to another location later.

#### Acceptance Criteria

1. WHEN the user activates copy with Selected_Items, THE Clipboard_State SHALL store the Selected_Items and set operation type to "copy"
2. WHEN the user copies new items, THE Clipboard_State SHALL replace previously stored items
3. THE Clipboard_State SHALL maintain clipboard contents until a paste operation completes or the user cuts/copies new items

### Requirement 13: Clipboard Paste Operation

**User Story:** As a user, I want to paste clipboard files into a folder, so that I can complete move or copy operations.

#### Acceptance Criteria

1. WHEN the user activates paste in a Target_Folder and Clipboard_State operation type is "move", THE File_Service SHALL move all clipboard items to the Target_Folder
2. WHEN the user activates paste in a Target_Folder and Clipboard_State operation type is "copy", THE File_Service SHALL copy all clipboard items to the Target_Folder
3. WHEN a paste operation completes successfully with operation type "move", THE Clipboard_State SHALL clear the clipboard contents
4. WHEN a paste operation completes successfully with operation type "copy", THE Clipboard_State SHALL retain the clipboard contents
5. IF the Clipboard_State is empty, THEN THE File_Manager SHALL not display the paste option

### Requirement 14: Drag and Drop Move

**User Story:** As a user, I want to drag files to a folder, so that I can move them with a natural gesture.

#### Acceptance Criteria

1. WHEN the user drags Selected_Items and drops them on a Target_Folder, THE File_Service SHALL move all Selected_Items to the Target_Folder
2. WHEN the user drags a single File_Entry and drops it on a Target_Folder, THE File_Service SHALL move that File_Entry to the Target_Folder
3. WHILE the user is dragging items, THE File_Manager SHALL display a visual indicator showing the dragged items
4. WHILE the user is dragging items over a valid Target_Folder, THE File_Manager SHALL highlight that folder
5. IF the user drops items on an invalid target, THEN THE File_Service SHALL not perform any operation

### Requirement 15: Prevent Invalid Move Operations

**User Story:** As a developer, I want the system to prevent invalid move operations, so that data integrity is maintained.

#### Acceptance Criteria

1. IF a move operation would place a folder inside itself, THEN THE File_Service SHALL return an error and not perform the move
2. IF a move operation would place a folder inside one of its descendants, THEN THE File_Service SHALL return an error and not perform the move
3. IF a move operation targets a non-existent folder, THEN THE File_Service SHALL return an error and not perform the move
4. WHEN the File_Service detects an invalid move operation, THE File_Service SHALL return a descriptive error message

### Requirement 16: Context Menu Display

**User Story:** As a user, I want to access file operations through a context menu, so that I can perform actions conveniently.

#### Acceptance Criteria

1. WHEN the user long-presses a File_Entry on mobile, THE File_Manager SHALL display a context menu
2. WHEN the user right-clicks a File_Entry on desktop, THE File_Manager SHALL display a context menu
3. THE File_Manager SHALL include Rename, Delete, Copy, Cut, and Move options in the context menu
4. IF the Clipboard_State contains items, THEN THE File_Manager SHALL include a Paste option in the context menu
5. IF the Clipboard_State is empty, THEN THE File_Manager SHALL not display a Paste option in the context menu

### Requirement 17: Folder Navigation

**User Story:** As a user, I want to navigate through folders, so that I can browse my file structure.

#### Acceptance Criteria

1. WHEN the user taps a folder File_Entry, THE File_Manager SHALL display the contents of that folder
2. WHEN the user navigates to a folder, THE File_Manager SHALL display a breadcrumb trail showing the current path
3. WHEN the user taps a breadcrumb segment, THE File_Manager SHALL navigate to that folder
4. WHEN the user activates back navigation, THE File_Manager SHALL navigate to the parent folder
5. THE File_Manager SHALL load and display folder contents within 500ms for folders containing up to 1000 files

### Requirement 18: Performance Under Load

**User Story:** As a user, I want the file manager to remain responsive with many files, so that I can work efficiently.

#### Acceptance Criteria

1. THE File_Manager SHALL render a list of 1000 File_Entry items within 1 second of navigation
2. WHEN the user scrolls through a list of 1000 File_Entry items, THE File_Manager SHALL maintain 60 frames per second
3. WHEN the Selection_System updates selection state, THE File_Manager SHALL update visual indicators within 16ms
4. THE File_Manager SHALL use lazy loading to render only visible File_Entry items
5. WHEN the user performs a batch operation on 100 files, THE File_Manager SHALL not block the UI thread for more than 100ms

### Requirement 19: State Management Integration

**User Story:** As a developer, I want file manager state managed through Riverpod, so that it integrates with the existing app architecture.

#### Acceptance Criteria

1. THE File_Manager SHALL use Riverpod providers for Selection_System state
2. THE File_Manager SHALL use Riverpod providers for Clipboard_State
3. THE File_Manager SHALL use Riverpod providers for current folder navigation state
4. WHEN state changes occur, THE File_Manager SHALL rebuild only affected UI components
5. THE File_Manager SHALL not rebuild the entire file list when selection state changes

### Requirement 20: Data Integrity During Operations

**User Story:** As a user, I want file operations to be reliable, so that I don't lose data.

#### Acceptance Criteria

1. WHEN a file operation fails, THE File_Service SHALL not modify the App_Database
2. WHEN a batch operation partially fails, THE File_Service SHALL complete all successful operations and report failures
3. THE File_Service SHALL validate all file paths before performing operations
4. THE File_Service SHALL validate all file names before performing operations
5. FOR ALL file operations, THE File_Service SHALL ensure the App_Database remains in a consistent state

### Requirement 21: Server Synchronization

**User Story:** As a user, I want my file operations to sync to the server, so that changes are preserved across devices.

#### Acceptance Criteria

1. WHEN the File_Service completes a move operation, THE File_Service SHALL add a move mutation to the Mutation_Queue
2. WHEN the File_Service completes a copy operation, THE File_Service SHALL add a create mutation to the Mutation_Queue
3. WHEN the File_Service completes a delete operation, THE File_Service SHALL add a delete mutation to the Mutation_Queue
4. WHEN the File_Service completes a rename operation, THE File_Service SHALL add an update mutation to the Mutation_Queue
5. FOR ALL batch operations, THE File_Service SHALL add individual mutations for each successful operation to the Mutation_Queue

### Requirement 22: Error Handling and Reporting

**User Story:** As a user, I want clear error messages when operations fail, so that I understand what went wrong.

#### Acceptance Criteria

1. WHEN a file operation fails, THE File_Service SHALL return an error object with a descriptive message
2. WHEN a batch operation fails, THE File_Service SHALL return a list of failed items with individual error messages
3. WHEN the File_Manager receives an error from File_Service, THE File_Manager SHALL display the error message to the user
4. THE File_Service SHALL distinguish between validation errors, permission errors, and system errors
5. THE File_Service SHALL log all errors with sufficient context for debugging

### Requirement 23: Selection Persistence During Navigation

**User Story:** As a user, I want my selection to clear when I navigate to a different folder, so that I don't accidentally operate on files in the wrong location.

#### Acceptance Criteria

1. WHEN the user navigates to a different folder, THE Selection_System SHALL clear the selected set
2. WHEN the user navigates back to a previous folder, THE Selection_System SHALL not restore previous selections
3. THE Clipboard_State SHALL persist across folder navigation until explicitly cleared

### Requirement 24: Visual Feedback During Operations

**User Story:** As a user, I want visual feedback during file operations, so that I know the system is working.

#### Acceptance Criteria

1. WHEN a file operation begins, THE File_Manager SHALL display a loading indicator
2. WHEN a batch operation is in progress, THE File_Manager SHALL display progress information
3. WHEN a file operation completes successfully, THE File_Manager SHALL display a success message
4. WHEN a file operation fails, THE File_Manager SHALL display an error message
5. THE File_Manager SHALL dismiss success messages automatically after 3 seconds

