import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';

/// Compute SHA-256 hash of bytes, prefixed with 'sha256:'.
String sha256Hex(List<int> bytes) {
  return 'sha256:${sha256.convert(bytes).toString()}';
}

/// Compute SHA-256 hash of a file, prefixed with 'sha256:'.
Future<String> sha256File(File file) async {
  final bytes = await file.readAsBytes();
  return sha256Hex(bytes);
}

/// Compute SHA-256 hash of a string (UTF-8 encoded).
String sha256String(String content) {
  return sha256Hex(utf8.encode(content));
}
