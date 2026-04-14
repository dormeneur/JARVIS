import 'package:drift/drift.dart';

/// Drift table for managing chat sessions.
class ChatSessions extends Table {
  TextColumn get id => text()(); // UUID
  TextColumn get title => text()();
  TextColumn get createdAt => text()(); // ISO8601 UTC
  TextColumn get lastActiveAt => text()(); // ISO8601 UTC

  @override
  Set<Column> get primaryKey => {id};
}
