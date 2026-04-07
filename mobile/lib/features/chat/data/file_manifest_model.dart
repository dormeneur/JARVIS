class FileManifestItem {
  final String path;
  final String content;
  final String type;

  const FileManifestItem({
    required this.path,
    required this.content,
    required this.type,
  });

  factory FileManifestItem.fromJson(Map<String, dynamic> json) {
    return FileManifestItem(
      path: json['path'] as String,
      content: json['content'] as String? ?? '',
      type: json['type'] as String? ?? 'file',
    );
  }

  Map<String, dynamic> toJson() {
    return {
      'path': path,
      'content': content,
      'type': type,
    };
  }
}
