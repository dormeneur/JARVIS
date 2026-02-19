/// Format a DateTime as ISO8601 UTC with Z suffix.
String toUtcIso8601(DateTime dt) {
  return dt.toUtc().toIso8601String().replaceFirst(RegExp(r'\+00:00$'), 'Z');
}

/// Parse an ISO8601 string to DateTime (always UTC).
DateTime parseUtcIso8601(String s) {
  return DateTime.parse(s).toUtc();
}

/// Current UTC time as ISO8601 with Z suffix.
String nowUtcIso8601() => toUtcIso8601(DateTime.now());

/// Format a file size in human readable form.
String formatFileSize(int? bytes) {
  if (bytes == null) return '';
  if (bytes < 1024) return '$bytes B';
  if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(1)} KB';
  return '${(bytes / (1024 * 1024)).toStringAsFixed(1)} MB';
}
