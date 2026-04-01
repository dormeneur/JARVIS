import 'package:flutter/material.dart';
import 'package:jarvis_mobile/features/explorer/data/explorer_repository.dart';
import 'package:jarvis_mobile/shared/models/file_entry.dart';
import 'package:jarvis_mobile/shared/utils/date_utils.dart';

/// Material SearchDelegate for deep file search across all directories.
///
/// Searches file names as the user types and displays results with
/// full path information. Tapping a result navigates to its parent
/// directory and optionally opens the file.
class FileSearchDelegate extends SearchDelegate<FileEntry?> {
  final ExplorerRepository repository;
  final void Function(String dir) onNavigate;
  final void Function(FileEntry entry) onOpen;

  FileSearchDelegate({
    required this.repository,
    required this.onNavigate,
    required this.onOpen,
  }) : super(
          searchFieldLabel: 'Search files and folders…',
          searchFieldStyle: const TextStyle(fontSize: 16),
        );

  @override
  List<Widget>? buildActions(BuildContext context) {
    return [
      if (query.isNotEmpty)
        IconButton(
          icon: const Icon(Icons.clear),
          onPressed: () {
            query = '';
            showSuggestions(context);
          },
        ),
    ];
  }

  @override
  Widget? buildLeading(BuildContext context) {
    return IconButton(
      icon: const Icon(Icons.arrow_back),
      onPressed: () => close(context, null),
    );
  }

  @override
  Widget buildResults(BuildContext context) {
    return _buildSearchResults(context);
  }

  @override
  Widget buildSuggestions(BuildContext context) {
    if (query.isEmpty) {
      return Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              Icons.search,
              size: 64,
              color: Theme.of(context).colorScheme.onSurfaceVariant.withValues(alpha: 0.3),
            ),
            const SizedBox(height: 16),
            Text(
              'Search your vault',
              style: Theme.of(context).textTheme.bodyLarge?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant,
                  ),
            ),
            const SizedBox(height: 4),
            Text(
              'Search across all folders and files',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: Theme.of(context).colorScheme.onSurfaceVariant.withValues(alpha: 0.7),
                  ),
            ),
          ],
        ),
      );
    }
    return _buildSearchResults(context);
  }

  Widget _buildSearchResults(BuildContext context) {
    final theme = Theme.of(context);

    return FutureBuilder<List<FileEntry>>(
      future: repository.searchFiles(query),
      builder: (context, snapshot) {
        if (snapshot.connectionState == ConnectionState.waiting) {
          return const Center(child: CircularProgressIndicator());
        }

        final results = snapshot.data ?? [];

        if (results.isEmpty) {
          return Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Icon(
                  Icons.search_off,
                  size: 48,
                  color: theme.colorScheme.onSurfaceVariant.withValues(alpha: 0.3),
                ),
                const SizedBox(height: 12),
                Text(
                  'No results for "$query"',
                  style: theme.textTheme.bodyLarge?.copyWith(
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
              ],
            ),
          );
        }

        return ListView.builder(
          itemCount: results.length,
          itemBuilder: (context, index) {
            final entry = results[index];
            final parentPath = _getParentPath(entry.path);

            return ListTile(
              leading: Icon(
                entry.isDirectory ? Icons.folder : Icons.description_outlined,
                color: entry.isDirectory
                    ? theme.colorScheme.primary
                    : theme.colorScheme.onSurfaceVariant,
              ),
              title: _buildHighlightedName(entry.name, query, theme),
              subtitle: Text(
                parentPath.isEmpty ? 'Vault root' : parentPath,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: theme.colorScheme.onSurfaceVariant,
                ),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              trailing: entry.isFile
                  ? Text(
                      formatFileSize(entry.sizeBytes),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    )
                  : const Icon(Icons.chevron_right, size: 20),
              onTap: () {
                close(context, entry);
                if (entry.isDirectory) {
                  onNavigate(entry.path);
                } else {
                  // Navigate to parent folder and open the file
                  onNavigate(parentPath);
                  onOpen(entry);
                }
              },
            );
          },
        );
      },
    );
  }

  String _getParentPath(String path) {
    final parts = path.split('/');
    if (parts.length <= 1) return '';
    return parts.sublist(0, parts.length - 1).join('/');
  }

  /// Builds a RichText with the matching query portion highlighted.
  Widget _buildHighlightedName(String name, String query, ThemeData theme) {
    if (query.isEmpty) return Text(name);

    final lowerName = name.toLowerCase();
    final lowerQuery = query.toLowerCase();
    final matchStart = lowerName.indexOf(lowerQuery);

    if (matchStart == -1) return Text(name);

    return RichText(
      text: TextSpan(
        style: theme.textTheme.bodyLarge,
        children: [
          if (matchStart > 0)
            TextSpan(text: name.substring(0, matchStart)),
          TextSpan(
            text: name.substring(matchStart, matchStart + query.length),
            style: TextStyle(
              fontWeight: FontWeight.w700,
              color: theme.colorScheme.primary,
            ),
          ),
          if (matchStart + query.length < name.length)
            TextSpan(
                text: name.substring(matchStart + query.length)),
        ],
      ),
      maxLines: 1,
      overflow: TextOverflow.ellipsis,
    );
  }
}
