import 'package:drift/drift.dart';

/// Drift table for persisting AI chat history.
class ChatMessages extends Table {
  IntColumn get id => integer().autoIncrement()();
  TextColumn get query => text()();
  TextColumn get response => text()();
  TextColumn get sources => text().nullable()(); // JSON-encoded list
  TextColumn get attachments => text().nullable()(); // JSON-encoded list of paths
  TextColumn get timestamp => text()(); // ISO8601 UTC
}
