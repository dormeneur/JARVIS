/// Represents a file or directory entry in the vault.
class FileEntry {
  final String path;
  final String name;
  final String type; // 'file' or 'directory'
  final int? sizeBytes;
  final String lastModified; // ISO8601 UTC with Z suffix
  final String? contentHash;
  final String? localPath;
  final String? lastSynced;

  const FileEntry({
    required this.path,
    required this.name,
    required this.type,
    this.sizeBytes,
    required this.lastModified,
    this.contentHash,
    this.localPath,
    this.lastSynced,
  });

  bool get isDirectory => type == 'directory';
  bool get isFile => type == 'file';
  bool get isSynced => localPath != null && localPath!.isNotEmpty;

  FileEntry copyWith({
    String? path,
    String? name,
    String? type,
    int? sizeBytes,
    String? lastModified,
    String? contentHash,
    String? localPath,
    String? lastSynced,
  }) {
    return FileEntry(
      path: path ?? this.path,
      name: name ?? this.name,
      type: type ?? this.type,
      sizeBytes: sizeBytes ?? this.sizeBytes,
      lastModified: lastModified ?? this.lastModified,
      contentHash: contentHash ?? this.contentHash,
      localPath: localPath ?? this.localPath,
      lastSynced: lastSynced ?? this.lastSynced,
    );
  }

  Map<String, dynamic> toManifestEntry() => {
    'path': path,
    'content_hash': contentHash ?? '',
    'last_modified': lastModified,
  };

  @override
  String toString() => 'FileEntry($path, $type)';
}
