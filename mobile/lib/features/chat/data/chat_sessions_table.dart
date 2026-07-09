import 'package:drift/drift.dart';

/// Drift table for managing chat sessions.
class ChatSessions extends Table {
  TextColumn get id => text()(); // UUID
  TextColumn get title => text()();
  TextColumn get createdAt => text()(); // ISO8601 UTC
  TextColumn get lastActiveAt => text()(); // ISO8601 UTC
  /// Session status: 'active' (current), 'inactive' (past/read-only).
  /// Future: 'resumed' when continuing a past session.
  TextColumn get status => text().withDefault(const Constant('active'))();

  @override
  Set<Column> get primaryKey => {id};
}
