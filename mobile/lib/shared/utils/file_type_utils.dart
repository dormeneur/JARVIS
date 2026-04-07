/// Utility to determine how a file should be opened based on its extension.
library;
/// Extensions that should open in the text editor.
const textExtensions = {
  'md', 'txt', 'log', 'json', 'yaml', 'yml', 'xml', 'toml',
  'dart', 'py', 'js', 'ts', 'java', 'kt', 'swift', 'rs',
  'go', 'c', 'cpp', 'h', 'css', 'html', 'csv', 'ini', 'cfg',
  'sh', 'bat', 'ps1', 'env', 'properties',
};

/// Extensions that open in the native file viewer (PDF, images, DOCX...).
const viewerExtensions = {
  'pdf', 'docx',
  'jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp',
};

/// Returns the lowercase extension for a file name (without the dot).
String fileExtension(String fileName) {
  if (!fileName.contains('.')) return '';
  return fileName.split('.').last.toLowerCase();
}

enum FileOpenMode { editor, viewer, unsupported }

/// Decides whether a file should open in the editor, the native viewer,
/// or not at all.
FileOpenMode fileOpenMode(String fileName) {
  final ext = fileExtension(fileName);
  if (ext.isEmpty || textExtensions.contains(ext)) return FileOpenMode.editor;
  if (viewerExtensions.contains(ext)) return FileOpenMode.viewer;
  return FileOpenMode.unsupported;
}
