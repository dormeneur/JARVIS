// GENERATED CODE - DO NOT MODIFY BY HAND

part of 'app_database.dart';

// ignore_for_file: type=lint
class $FileCacheEntriesTable extends FileCacheEntries
    with TableInfo<$FileCacheEntriesTable, FileCacheEntry> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $FileCacheEntriesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _pathMeta = const VerificationMeta('path');
  @override
  late final GeneratedColumn<String> path = GeneratedColumn<String>(
    'path',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _nameMeta = const VerificationMeta('name');
  @override
  late final GeneratedColumn<String> name = GeneratedColumn<String>(
    'name',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _typeMeta = const VerificationMeta('type');
  @override
  late final GeneratedColumn<String> type = GeneratedColumn<String>(
    'type',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _sizeBytesMeta = const VerificationMeta(
    'sizeBytes',
  );
  @override
  late final GeneratedColumn<int> sizeBytes = GeneratedColumn<int>(
    'size_bytes',
    aliasedName,
    true,
    type: DriftSqlType.int,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _lastModifiedMeta = const VerificationMeta(
    'lastModified',
  );
  @override
  late final GeneratedColumn<String> lastModified = GeneratedColumn<String>(
    'last_modified',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _contentHashMeta = const VerificationMeta(
    'contentHash',
  );
  @override
  late final GeneratedColumn<String> contentHash = GeneratedColumn<String>(
    'content_hash',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _localPathMeta = const VerificationMeta(
    'localPath',
  );
  @override
  late final GeneratedColumn<String> localPath = GeneratedColumn<String>(
    'local_path',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _lastSyncedMeta = const VerificationMeta(
    'lastSynced',
  );
  @override
  late final GeneratedColumn<String> lastSynced = GeneratedColumn<String>(
    'last_synced',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  @override
  List<GeneratedColumn> get $columns => [
    path,
    name,
    type,
    sizeBytes,
    lastModified,
    contentHash,
    localPath,
    lastSynced,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'file_cache_entries';
  @override
  VerificationContext validateIntegrity(
    Insertable<FileCacheEntry> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('path')) {
      context.handle(
        _pathMeta,
        path.isAcceptableOrUnknown(data['path']!, _pathMeta),
      );
    } else if (isInserting) {
      context.missing(_pathMeta);
    }
    if (data.containsKey('name')) {
      context.handle(
        _nameMeta,
        name.isAcceptableOrUnknown(data['name']!, _nameMeta),
      );
    } else if (isInserting) {
      context.missing(_nameMeta);
    }
    if (data.containsKey('type')) {
      context.handle(
        _typeMeta,
        type.isAcceptableOrUnknown(data['type']!, _typeMeta),
      );
    } else if (isInserting) {
      context.missing(_typeMeta);
    }
    if (data.containsKey('size_bytes')) {
      context.handle(
        _sizeBytesMeta,
        sizeBytes.isAcceptableOrUnknown(data['size_bytes']!, _sizeBytesMeta),
      );
    }
    if (data.containsKey('last_modified')) {
      context.handle(
        _lastModifiedMeta,
        lastModified.isAcceptableOrUnknown(
          data['last_modified']!,
          _lastModifiedMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_lastModifiedMeta);
    }
    if (data.containsKey('content_hash')) {
      context.handle(
        _contentHashMeta,
        contentHash.isAcceptableOrUnknown(
          data['content_hash']!,
          _contentHashMeta,
        ),
      );
    }
    if (data.containsKey('local_path')) {
      context.handle(
        _localPathMeta,
        localPath.isAcceptableOrUnknown(data['local_path']!, _localPathMeta),
      );
    }
    if (data.containsKey('last_synced')) {
      context.handle(
        _lastSyncedMeta,
        lastSynced.isAcceptableOrUnknown(data['last_synced']!, _lastSyncedMeta),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {path};
  @override
  FileCacheEntry map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return FileCacheEntry(
      path: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}path'],
      )!,
      name: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}name'],
      )!,
      type: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}type'],
      )!,
      sizeBytes: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}size_bytes'],
      ),
      lastModified: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}last_modified'],
      )!,
      contentHash: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}content_hash'],
      ),
      localPath: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_path'],
      ),
      lastSynced: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}last_synced'],
      ),
    );
  }

  @override
  $FileCacheEntriesTable createAlias(String alias) {
    return $FileCacheEntriesTable(attachedDatabase, alias);
  }
}

class FileCacheEntry extends DataClass implements Insertable<FileCacheEntry> {
  final String path;
  final String name;
  final String type;
  final int? sizeBytes;
  final String lastModified;
  final String? contentHash;
  final String? localPath;
  final String? lastSynced;
  const FileCacheEntry({
    required this.path,
    required this.name,
    required this.type,
    this.sizeBytes,
    required this.lastModified,
    this.contentHash,
    this.localPath,
    this.lastSynced,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['path'] = Variable<String>(path);
    map['name'] = Variable<String>(name);
    map['type'] = Variable<String>(type);
    if (!nullToAbsent || sizeBytes != null) {
      map['size_bytes'] = Variable<int>(sizeBytes);
    }
    map['last_modified'] = Variable<String>(lastModified);
    if (!nullToAbsent || contentHash != null) {
      map['content_hash'] = Variable<String>(contentHash);
    }
    if (!nullToAbsent || localPath != null) {
      map['local_path'] = Variable<String>(localPath);
    }
    if (!nullToAbsent || lastSynced != null) {
      map['last_synced'] = Variable<String>(lastSynced);
    }
    return map;
  }

  FileCacheEntriesCompanion toCompanion(bool nullToAbsent) {
    return FileCacheEntriesCompanion(
      path: Value(path),
      name: Value(name),
      type: Value(type),
      sizeBytes: sizeBytes == null && nullToAbsent
          ? const Value.absent()
          : Value(sizeBytes),
      lastModified: Value(lastModified),
      contentHash: contentHash == null && nullToAbsent
          ? const Value.absent()
          : Value(contentHash),
      localPath: localPath == null && nullToAbsent
          ? const Value.absent()
          : Value(localPath),
      lastSynced: lastSynced == null && nullToAbsent
          ? const Value.absent()
          : Value(lastSynced),
    );
  }

  factory FileCacheEntry.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return FileCacheEntry(
      path: serializer.fromJson<String>(json['path']),
      name: serializer.fromJson<String>(json['name']),
      type: serializer.fromJson<String>(json['type']),
      sizeBytes: serializer.fromJson<int?>(json['sizeBytes']),
      lastModified: serializer.fromJson<String>(json['lastModified']),
      contentHash: serializer.fromJson<String?>(json['contentHash']),
      localPath: serializer.fromJson<String?>(json['localPath']),
      lastSynced: serializer.fromJson<String?>(json['lastSynced']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'path': serializer.toJson<String>(path),
      'name': serializer.toJson<String>(name),
      'type': serializer.toJson<String>(type),
      'sizeBytes': serializer.toJson<int?>(sizeBytes),
      'lastModified': serializer.toJson<String>(lastModified),
      'contentHash': serializer.toJson<String?>(contentHash),
      'localPath': serializer.toJson<String?>(localPath),
      'lastSynced': serializer.toJson<String?>(lastSynced),
    };
  }

  FileCacheEntry copyWith({
    String? path,
    String? name,
    String? type,
    Value<int?> sizeBytes = const Value.absent(),
    String? lastModified,
    Value<String?> contentHash = const Value.absent(),
    Value<String?> localPath = const Value.absent(),
    Value<String?> lastSynced = const Value.absent(),
  }) => FileCacheEntry(
    path: path ?? this.path,
    name: name ?? this.name,
    type: type ?? this.type,
    sizeBytes: sizeBytes.present ? sizeBytes.value : this.sizeBytes,
    lastModified: lastModified ?? this.lastModified,
    contentHash: contentHash.present ? contentHash.value : this.contentHash,
    localPath: localPath.present ? localPath.value : this.localPath,
    lastSynced: lastSynced.present ? lastSynced.value : this.lastSynced,
  );
  FileCacheEntry copyWithCompanion(FileCacheEntriesCompanion data) {
    return FileCacheEntry(
      path: data.path.present ? data.path.value : this.path,
      name: data.name.present ? data.name.value : this.name,
      type: data.type.present ? data.type.value : this.type,
      sizeBytes: data.sizeBytes.present ? data.sizeBytes.value : this.sizeBytes,
      lastModified: data.lastModified.present
          ? data.lastModified.value
          : this.lastModified,
      contentHash: data.contentHash.present
          ? data.contentHash.value
          : this.contentHash,
      localPath: data.localPath.present ? data.localPath.value : this.localPath,
      lastSynced: data.lastSynced.present
          ? data.lastSynced.value
          : this.lastSynced,
    );
  }

  @override
  String toString() {
    return (StringBuffer('FileCacheEntry(')
          ..write('path: $path, ')
          ..write('name: $name, ')
          ..write('type: $type, ')
          ..write('sizeBytes: $sizeBytes, ')
          ..write('lastModified: $lastModified, ')
          ..write('contentHash: $contentHash, ')
          ..write('localPath: $localPath, ')
          ..write('lastSynced: $lastSynced')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    path,
    name,
    type,
    sizeBytes,
    lastModified,
    contentHash,
    localPath,
    lastSynced,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is FileCacheEntry &&
          other.path == this.path &&
          other.name == this.name &&
          other.type == this.type &&
          other.sizeBytes == this.sizeBytes &&
          other.lastModified == this.lastModified &&
          other.contentHash == this.contentHash &&
          other.localPath == this.localPath &&
          other.lastSynced == this.lastSynced);
}

class FileCacheEntriesCompanion extends UpdateCompanion<FileCacheEntry> {
  final Value<String> path;
  final Value<String> name;
  final Value<String> type;
  final Value<int?> sizeBytes;
  final Value<String> lastModified;
  final Value<String?> contentHash;
  final Value<String?> localPath;
  final Value<String?> lastSynced;
  final Value<int> rowid;
  const FileCacheEntriesCompanion({
    this.path = const Value.absent(),
    this.name = const Value.absent(),
    this.type = const Value.absent(),
    this.sizeBytes = const Value.absent(),
    this.lastModified = const Value.absent(),
    this.contentHash = const Value.absent(),
    this.localPath = const Value.absent(),
    this.lastSynced = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  FileCacheEntriesCompanion.insert({
    required String path,
    required String name,
    required String type,
    this.sizeBytes = const Value.absent(),
    required String lastModified,
    this.contentHash = const Value.absent(),
    this.localPath = const Value.absent(),
    this.lastSynced = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : path = Value(path),
       name = Value(name),
       type = Value(type),
       lastModified = Value(lastModified);
  static Insertable<FileCacheEntry> custom({
    Expression<String>? path,
    Expression<String>? name,
    Expression<String>? type,
    Expression<int>? sizeBytes,
    Expression<String>? lastModified,
    Expression<String>? contentHash,
    Expression<String>? localPath,
    Expression<String>? lastSynced,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (path != null) 'path': path,
      if (name != null) 'name': name,
      if (type != null) 'type': type,
      if (sizeBytes != null) 'size_bytes': sizeBytes,
      if (lastModified != null) 'last_modified': lastModified,
      if (contentHash != null) 'content_hash': contentHash,
      if (localPath != null) 'local_path': localPath,
      if (lastSynced != null) 'last_synced': lastSynced,
      if (rowid != null) 'rowid': rowid,
    });
  }

  FileCacheEntriesCompanion copyWith({
    Value<String>? path,
    Value<String>? name,
    Value<String>? type,
    Value<int?>? sizeBytes,
    Value<String>? lastModified,
    Value<String?>? contentHash,
    Value<String?>? localPath,
    Value<String?>? lastSynced,
    Value<int>? rowid,
  }) {
    return FileCacheEntriesCompanion(
      path: path ?? this.path,
      name: name ?? this.name,
      type: type ?? this.type,
      sizeBytes: sizeBytes ?? this.sizeBytes,
      lastModified: lastModified ?? this.lastModified,
      contentHash: contentHash ?? this.contentHash,
      localPath: localPath ?? this.localPath,
      lastSynced: lastSynced ?? this.lastSynced,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (path.present) {
      map['path'] = Variable<String>(path.value);
    }
    if (name.present) {
      map['name'] = Variable<String>(name.value);
    }
    if (type.present) {
      map['type'] = Variable<String>(type.value);
    }
    if (sizeBytes.present) {
      map['size_bytes'] = Variable<int>(sizeBytes.value);
    }
    if (lastModified.present) {
      map['last_modified'] = Variable<String>(lastModified.value);
    }
    if (contentHash.present) {
      map['content_hash'] = Variable<String>(contentHash.value);
    }
    if (localPath.present) {
      map['local_path'] = Variable<String>(localPath.value);
    }
    if (lastSynced.present) {
      map['last_synced'] = Variable<String>(lastSynced.value);
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('FileCacheEntriesCompanion(')
          ..write('path: $path, ')
          ..write('name: $name, ')
          ..write('type: $type, ')
          ..write('sizeBytes: $sizeBytes, ')
          ..write('lastModified: $lastModified, ')
          ..write('contentHash: $contentHash, ')
          ..write('localPath: $localPath, ')
          ..write('lastSynced: $lastSynced, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

abstract class _$AppDatabase extends GeneratedDatabase {
  _$AppDatabase(QueryExecutor e) : super(e);
  $AppDatabaseManager get managers => $AppDatabaseManager(this);
  late final $FileCacheEntriesTable fileCacheEntries = $FileCacheEntriesTable(
    this,
  );
  @override
  Iterable<TableInfo<Table, Object?>> get allTables =>
      allSchemaEntities.whereType<TableInfo<Table, Object?>>();
  @override
  List<DatabaseSchemaEntity> get allSchemaEntities => [fileCacheEntries];
}

typedef $$FileCacheEntriesTableCreateCompanionBuilder =
    FileCacheEntriesCompanion Function({
      required String path,
      required String name,
      required String type,
      Value<int?> sizeBytes,
      required String lastModified,
      Value<String?> contentHash,
      Value<String?> localPath,
      Value<String?> lastSynced,
      Value<int> rowid,
    });
typedef $$FileCacheEntriesTableUpdateCompanionBuilder =
    FileCacheEntriesCompanion Function({
      Value<String> path,
      Value<String> name,
      Value<String> type,
      Value<int?> sizeBytes,
      Value<String> lastModified,
      Value<String?> contentHash,
      Value<String?> localPath,
      Value<String?> lastSynced,
      Value<int> rowid,
    });

class $$FileCacheEntriesTableFilterComposer
    extends Composer<_$AppDatabase, $FileCacheEntriesTable> {
  $$FileCacheEntriesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get path => $composableBuilder(
    column: $table.path,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get name => $composableBuilder(
    column: $table.name,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get type => $composableBuilder(
    column: $table.type,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get sizeBytes => $composableBuilder(
    column: $table.sizeBytes,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get lastModified => $composableBuilder(
    column: $table.lastModified,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get contentHash => $composableBuilder(
    column: $table.contentHash,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get localPath => $composableBuilder(
    column: $table.localPath,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get lastSynced => $composableBuilder(
    column: $table.lastSynced,
    builder: (column) => ColumnFilters(column),
  );
}

class $$FileCacheEntriesTableOrderingComposer
    extends Composer<_$AppDatabase, $FileCacheEntriesTable> {
  $$FileCacheEntriesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get path => $composableBuilder(
    column: $table.path,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get name => $composableBuilder(
    column: $table.name,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get type => $composableBuilder(
    column: $table.type,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get sizeBytes => $composableBuilder(
    column: $table.sizeBytes,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get lastModified => $composableBuilder(
    column: $table.lastModified,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get contentHash => $composableBuilder(
    column: $table.contentHash,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get localPath => $composableBuilder(
    column: $table.localPath,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get lastSynced => $composableBuilder(
    column: $table.lastSynced,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$FileCacheEntriesTableAnnotationComposer
    extends Composer<_$AppDatabase, $FileCacheEntriesTable> {
  $$FileCacheEntriesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get path =>
      $composableBuilder(column: $table.path, builder: (column) => column);

  GeneratedColumn<String> get name =>
      $composableBuilder(column: $table.name, builder: (column) => column);

  GeneratedColumn<String> get type =>
      $composableBuilder(column: $table.type, builder: (column) => column);

  GeneratedColumn<int> get sizeBytes =>
      $composableBuilder(column: $table.sizeBytes, builder: (column) => column);

  GeneratedColumn<String> get lastModified => $composableBuilder(
    column: $table.lastModified,
    builder: (column) => column,
  );

  GeneratedColumn<String> get contentHash => $composableBuilder(
    column: $table.contentHash,
    builder: (column) => column,
  );

  GeneratedColumn<String> get localPath =>
      $composableBuilder(column: $table.localPath, builder: (column) => column);

  GeneratedColumn<String> get lastSynced => $composableBuilder(
    column: $table.lastSynced,
    builder: (column) => column,
  );
}

class $$FileCacheEntriesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $FileCacheEntriesTable,
          FileCacheEntry,
          $$FileCacheEntriesTableFilterComposer,
          $$FileCacheEntriesTableOrderingComposer,
          $$FileCacheEntriesTableAnnotationComposer,
          $$FileCacheEntriesTableCreateCompanionBuilder,
          $$FileCacheEntriesTableUpdateCompanionBuilder,
          (
            FileCacheEntry,
            BaseReferences<
              _$AppDatabase,
              $FileCacheEntriesTable,
              FileCacheEntry
            >,
          ),
          FileCacheEntry,
          PrefetchHooks Function()
        > {
  $$FileCacheEntriesTableTableManager(
    _$AppDatabase db,
    $FileCacheEntriesTable table,
  ) : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$FileCacheEntriesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$FileCacheEntriesTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$FileCacheEntriesTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<String> path = const Value.absent(),
                Value<String> name = const Value.absent(),
                Value<String> type = const Value.absent(),
                Value<int?> sizeBytes = const Value.absent(),
                Value<String> lastModified = const Value.absent(),
                Value<String?> contentHash = const Value.absent(),
                Value<String?> localPath = const Value.absent(),
                Value<String?> lastSynced = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => FileCacheEntriesCompanion(
                path: path,
                name: name,
                type: type,
                sizeBytes: sizeBytes,
                lastModified: lastModified,
                contentHash: contentHash,
                localPath: localPath,
                lastSynced: lastSynced,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String path,
                required String name,
                required String type,
                Value<int?> sizeBytes = const Value.absent(),
                required String lastModified,
                Value<String?> contentHash = const Value.absent(),
                Value<String?> localPath = const Value.absent(),
                Value<String?> lastSynced = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => FileCacheEntriesCompanion.insert(
                path: path,
                name: name,
                type: type,
                sizeBytes: sizeBytes,
                lastModified: lastModified,
                contentHash: contentHash,
                localPath: localPath,
                lastSynced: lastSynced,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$FileCacheEntriesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $FileCacheEntriesTable,
      FileCacheEntry,
      $$FileCacheEntriesTableFilterComposer,
      $$FileCacheEntriesTableOrderingComposer,
      $$FileCacheEntriesTableAnnotationComposer,
      $$FileCacheEntriesTableCreateCompanionBuilder,
      $$FileCacheEntriesTableUpdateCompanionBuilder,
      (
        FileCacheEntry,
        BaseReferences<_$AppDatabase, $FileCacheEntriesTable, FileCacheEntry>,
      ),
      FileCacheEntry,
      PrefetchHooks Function()
    >;

class $AppDatabaseManager {
  final _$AppDatabase _db;
  $AppDatabaseManager(this._db);
  $$FileCacheEntriesTableTableManager get fileCacheEntries =>
      $$FileCacheEntriesTableTableManager(_db, _db.fileCacheEntries);
}
