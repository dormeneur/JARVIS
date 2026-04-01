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
  static const VerificationMeta _serverVersionMeta = const VerificationMeta(
    'serverVersion',
  );
  @override
  late final GeneratedColumn<int> serverVersion = GeneratedColumn<int>(
    'server_version',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: false,
    defaultValue: const Constant(1),
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
    serverVersion,
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
    if (data.containsKey('server_version')) {
      context.handle(
        _serverVersionMeta,
        serverVersion.isAcceptableOrUnknown(
          data['server_version']!,
          _serverVersionMeta,
        ),
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
      serverVersion: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}server_version'],
      )!,
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
  final int serverVersion;
  const FileCacheEntry({
    required this.path,
    required this.name,
    required this.type,
    this.sizeBytes,
    required this.lastModified,
    this.contentHash,
    this.localPath,
    this.lastSynced,
    required this.serverVersion,
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
    map['server_version'] = Variable<int>(serverVersion);
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
      serverVersion: Value(serverVersion),
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
      serverVersion: serializer.fromJson<int>(json['serverVersion']),
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
      'serverVersion': serializer.toJson<int>(serverVersion),
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
    int? serverVersion,
  }) => FileCacheEntry(
    path: path ?? this.path,
    name: name ?? this.name,
    type: type ?? this.type,
    sizeBytes: sizeBytes.present ? sizeBytes.value : this.sizeBytes,
    lastModified: lastModified ?? this.lastModified,
    contentHash: contentHash.present ? contentHash.value : this.contentHash,
    localPath: localPath.present ? localPath.value : this.localPath,
    lastSynced: lastSynced.present ? lastSynced.value : this.lastSynced,
    serverVersion: serverVersion ?? this.serverVersion,
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
      serverVersion: data.serverVersion.present
          ? data.serverVersion.value
          : this.serverVersion,
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
          ..write('lastSynced: $lastSynced, ')
          ..write('serverVersion: $serverVersion')
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
    serverVersion,
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
          other.lastSynced == this.lastSynced &&
          other.serverVersion == this.serverVersion);
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
  final Value<int> serverVersion;
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
    this.serverVersion = const Value.absent(),
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
    this.serverVersion = const Value.absent(),
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
    Expression<int>? serverVersion,
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
      if (serverVersion != null) 'server_version': serverVersion,
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
    Value<int>? serverVersion,
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
      serverVersion: serverVersion ?? this.serverVersion,
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
    if (serverVersion.present) {
      map['server_version'] = Variable<int>(serverVersion.value);
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
          ..write('serverVersion: $serverVersion, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $MutationQueueTable extends MutationQueue
    with TableInfo<$MutationQueueTable, MutationQueueData> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $MutationQueueTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _idMeta = const VerificationMeta('id');
  @override
  late final GeneratedColumn<String> id = GeneratedColumn<String>(
    'id',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _pathMeta = const VerificationMeta('path');
  @override
  late final GeneratedColumn<String> path = GeneratedColumn<String>(
    'path',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _operationMeta = const VerificationMeta(
    'operation',
  );
  @override
  late final GeneratedColumn<String> operation = GeneratedColumn<String>(
    'operation',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _timestampMeta = const VerificationMeta(
    'timestamp',
  );
  @override
  late final GeneratedColumn<String> timestamp = GeneratedColumn<String>(
    'timestamp',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _retryCountMeta = const VerificationMeta(
    'retryCount',
  );
  @override
  late final GeneratedColumn<int> retryCount = GeneratedColumn<int>(
    'retry_count',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: false,
    defaultValue: const Constant(0),
  );
  static const VerificationMeta _statusMeta = const VerificationMeta('status');
  @override
  late final GeneratedColumn<String> status = GeneratedColumn<String>(
    'status',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _baseVersionMeta = const VerificationMeta(
    'baseVersion',
  );
  @override
  late final GeneratedColumn<int> baseVersion = GeneratedColumn<int>(
    'base_version',
    aliasedName,
    false,
    type: DriftSqlType.int,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _conflictFilePathMeta = const VerificationMeta(
    'conflictFilePath',
  );
  @override
  late final GeneratedColumn<String> conflictFilePath = GeneratedColumn<String>(
    'conflict_file_path',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _localContentSnapshotMeta =
      const VerificationMeta('localContentSnapshot');
  @override
  late final GeneratedColumn<String> localContentSnapshot =
      GeneratedColumn<String>(
        'local_content_snapshot',
        aliasedName,
        true,
        type: DriftSqlType.string,
        requiredDuringInsert: false,
      );
  @override
  List<GeneratedColumn> get $columns => [
    id,
    path,
    operation,
    timestamp,
    retryCount,
    status,
    baseVersion,
    conflictFilePath,
    localContentSnapshot,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'mutation_queue';
  @override
  VerificationContext validateIntegrity(
    Insertable<MutationQueueData> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('id')) {
      context.handle(_idMeta, id.isAcceptableOrUnknown(data['id']!, _idMeta));
    } else if (isInserting) {
      context.missing(_idMeta);
    }
    if (data.containsKey('path')) {
      context.handle(
        _pathMeta,
        path.isAcceptableOrUnknown(data['path']!, _pathMeta),
      );
    } else if (isInserting) {
      context.missing(_pathMeta);
    }
    if (data.containsKey('operation')) {
      context.handle(
        _operationMeta,
        operation.isAcceptableOrUnknown(data['operation']!, _operationMeta),
      );
    } else if (isInserting) {
      context.missing(_operationMeta);
    }
    if (data.containsKey('timestamp')) {
      context.handle(
        _timestampMeta,
        timestamp.isAcceptableOrUnknown(data['timestamp']!, _timestampMeta),
      );
    } else if (isInserting) {
      context.missing(_timestampMeta);
    }
    if (data.containsKey('retry_count')) {
      context.handle(
        _retryCountMeta,
        retryCount.isAcceptableOrUnknown(data['retry_count']!, _retryCountMeta),
      );
    }
    if (data.containsKey('status')) {
      context.handle(
        _statusMeta,
        status.isAcceptableOrUnknown(data['status']!, _statusMeta),
      );
    } else if (isInserting) {
      context.missing(_statusMeta);
    }
    if (data.containsKey('base_version')) {
      context.handle(
        _baseVersionMeta,
        baseVersion.isAcceptableOrUnknown(
          data['base_version']!,
          _baseVersionMeta,
        ),
      );
    } else if (isInserting) {
      context.missing(_baseVersionMeta);
    }
    if (data.containsKey('conflict_file_path')) {
      context.handle(
        _conflictFilePathMeta,
        conflictFilePath.isAcceptableOrUnknown(
          data['conflict_file_path']!,
          _conflictFilePathMeta,
        ),
      );
    }
    if (data.containsKey('local_content_snapshot')) {
      context.handle(
        _localContentSnapshotMeta,
        localContentSnapshot.isAcceptableOrUnknown(
          data['local_content_snapshot']!,
          _localContentSnapshotMeta,
        ),
      );
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {id};
  @override
  MutationQueueData map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return MutationQueueData(
      id: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}id'],
      )!,
      path: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}path'],
      )!,
      operation: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}operation'],
      )!,
      timestamp: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}timestamp'],
      )!,
      retryCount: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}retry_count'],
      )!,
      status: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}status'],
      )!,
      baseVersion: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}base_version'],
      )!,
      conflictFilePath: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}conflict_file_path'],
      ),
      localContentSnapshot: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}local_content_snapshot'],
      ),
    );
  }

  @override
  $MutationQueueTable createAlias(String alias) {
    return $MutationQueueTable(attachedDatabase, alias);
  }
}

class MutationQueueData extends DataClass
    implements Insertable<MutationQueueData> {
  final String id;
  final String path;
  final String operation;
  final String timestamp;
  final int retryCount;
  final String status;
  final int baseVersion;
  final String? conflictFilePath;
  final String? localContentSnapshot;
  const MutationQueueData({
    required this.id,
    required this.path,
    required this.operation,
    required this.timestamp,
    required this.retryCount,
    required this.status,
    required this.baseVersion,
    this.conflictFilePath,
    this.localContentSnapshot,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['id'] = Variable<String>(id);
    map['path'] = Variable<String>(path);
    map['operation'] = Variable<String>(operation);
    map['timestamp'] = Variable<String>(timestamp);
    map['retry_count'] = Variable<int>(retryCount);
    map['status'] = Variable<String>(status);
    map['base_version'] = Variable<int>(baseVersion);
    if (!nullToAbsent || conflictFilePath != null) {
      map['conflict_file_path'] = Variable<String>(conflictFilePath);
    }
    if (!nullToAbsent || localContentSnapshot != null) {
      map['local_content_snapshot'] = Variable<String>(localContentSnapshot);
    }
    return map;
  }

  MutationQueueCompanion toCompanion(bool nullToAbsent) {
    return MutationQueueCompanion(
      id: Value(id),
      path: Value(path),
      operation: Value(operation),
      timestamp: Value(timestamp),
      retryCount: Value(retryCount),
      status: Value(status),
      baseVersion: Value(baseVersion),
      conflictFilePath: conflictFilePath == null && nullToAbsent
          ? const Value.absent()
          : Value(conflictFilePath),
      localContentSnapshot: localContentSnapshot == null && nullToAbsent
          ? const Value.absent()
          : Value(localContentSnapshot),
    );
  }

  factory MutationQueueData.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return MutationQueueData(
      id: serializer.fromJson<String>(json['id']),
      path: serializer.fromJson<String>(json['path']),
      operation: serializer.fromJson<String>(json['operation']),
      timestamp: serializer.fromJson<String>(json['timestamp']),
      retryCount: serializer.fromJson<int>(json['retryCount']),
      status: serializer.fromJson<String>(json['status']),
      baseVersion: serializer.fromJson<int>(json['baseVersion']),
      conflictFilePath: serializer.fromJson<String?>(json['conflictFilePath']),
      localContentSnapshot: serializer.fromJson<String?>(
        json['localContentSnapshot'],
      ),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'id': serializer.toJson<String>(id),
      'path': serializer.toJson<String>(path),
      'operation': serializer.toJson<String>(operation),
      'timestamp': serializer.toJson<String>(timestamp),
      'retryCount': serializer.toJson<int>(retryCount),
      'status': serializer.toJson<String>(status),
      'baseVersion': serializer.toJson<int>(baseVersion),
      'conflictFilePath': serializer.toJson<String?>(conflictFilePath),
      'localContentSnapshot': serializer.toJson<String?>(localContentSnapshot),
    };
  }

  MutationQueueData copyWith({
    String? id,
    String? path,
    String? operation,
    String? timestamp,
    int? retryCount,
    String? status,
    int? baseVersion,
    Value<String?> conflictFilePath = const Value.absent(),
    Value<String?> localContentSnapshot = const Value.absent(),
  }) => MutationQueueData(
    id: id ?? this.id,
    path: path ?? this.path,
    operation: operation ?? this.operation,
    timestamp: timestamp ?? this.timestamp,
    retryCount: retryCount ?? this.retryCount,
    status: status ?? this.status,
    baseVersion: baseVersion ?? this.baseVersion,
    conflictFilePath: conflictFilePath.present
        ? conflictFilePath.value
        : this.conflictFilePath,
    localContentSnapshot: localContentSnapshot.present
        ? localContentSnapshot.value
        : this.localContentSnapshot,
  );
  MutationQueueData copyWithCompanion(MutationQueueCompanion data) {
    return MutationQueueData(
      id: data.id.present ? data.id.value : this.id,
      path: data.path.present ? data.path.value : this.path,
      operation: data.operation.present ? data.operation.value : this.operation,
      timestamp: data.timestamp.present ? data.timestamp.value : this.timestamp,
      retryCount: data.retryCount.present
          ? data.retryCount.value
          : this.retryCount,
      status: data.status.present ? data.status.value : this.status,
      baseVersion: data.baseVersion.present
          ? data.baseVersion.value
          : this.baseVersion,
      conflictFilePath: data.conflictFilePath.present
          ? data.conflictFilePath.value
          : this.conflictFilePath,
      localContentSnapshot: data.localContentSnapshot.present
          ? data.localContentSnapshot.value
          : this.localContentSnapshot,
    );
  }

  @override
  String toString() {
    return (StringBuffer('MutationQueueData(')
          ..write('id: $id, ')
          ..write('path: $path, ')
          ..write('operation: $operation, ')
          ..write('timestamp: $timestamp, ')
          ..write('retryCount: $retryCount, ')
          ..write('status: $status, ')
          ..write('baseVersion: $baseVersion, ')
          ..write('conflictFilePath: $conflictFilePath, ')
          ..write('localContentSnapshot: $localContentSnapshot')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode => Object.hash(
    id,
    path,
    operation,
    timestamp,
    retryCount,
    status,
    baseVersion,
    conflictFilePath,
    localContentSnapshot,
  );
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is MutationQueueData &&
          other.id == this.id &&
          other.path == this.path &&
          other.operation == this.operation &&
          other.timestamp == this.timestamp &&
          other.retryCount == this.retryCount &&
          other.status == this.status &&
          other.baseVersion == this.baseVersion &&
          other.conflictFilePath == this.conflictFilePath &&
          other.localContentSnapshot == this.localContentSnapshot);
}

class MutationQueueCompanion extends UpdateCompanion<MutationQueueData> {
  final Value<String> id;
  final Value<String> path;
  final Value<String> operation;
  final Value<String> timestamp;
  final Value<int> retryCount;
  final Value<String> status;
  final Value<int> baseVersion;
  final Value<String?> conflictFilePath;
  final Value<String?> localContentSnapshot;
  final Value<int> rowid;
  const MutationQueueCompanion({
    this.id = const Value.absent(),
    this.path = const Value.absent(),
    this.operation = const Value.absent(),
    this.timestamp = const Value.absent(),
    this.retryCount = const Value.absent(),
    this.status = const Value.absent(),
    this.baseVersion = const Value.absent(),
    this.conflictFilePath = const Value.absent(),
    this.localContentSnapshot = const Value.absent(),
    this.rowid = const Value.absent(),
  });
  MutationQueueCompanion.insert({
    required String id,
    required String path,
    required String operation,
    required String timestamp,
    this.retryCount = const Value.absent(),
    required String status,
    required int baseVersion,
    this.conflictFilePath = const Value.absent(),
    this.localContentSnapshot = const Value.absent(),
    this.rowid = const Value.absent(),
  }) : id = Value(id),
       path = Value(path),
       operation = Value(operation),
       timestamp = Value(timestamp),
       status = Value(status),
       baseVersion = Value(baseVersion);
  static Insertable<MutationQueueData> custom({
    Expression<String>? id,
    Expression<String>? path,
    Expression<String>? operation,
    Expression<String>? timestamp,
    Expression<int>? retryCount,
    Expression<String>? status,
    Expression<int>? baseVersion,
    Expression<String>? conflictFilePath,
    Expression<String>? localContentSnapshot,
    Expression<int>? rowid,
  }) {
    return RawValuesInsertable({
      if (id != null) 'id': id,
      if (path != null) 'path': path,
      if (operation != null) 'operation': operation,
      if (timestamp != null) 'timestamp': timestamp,
      if (retryCount != null) 'retry_count': retryCount,
      if (status != null) 'status': status,
      if (baseVersion != null) 'base_version': baseVersion,
      if (conflictFilePath != null) 'conflict_file_path': conflictFilePath,
      if (localContentSnapshot != null)
        'local_content_snapshot': localContentSnapshot,
      if (rowid != null) 'rowid': rowid,
    });
  }

  MutationQueueCompanion copyWith({
    Value<String>? id,
    Value<String>? path,
    Value<String>? operation,
    Value<String>? timestamp,
    Value<int>? retryCount,
    Value<String>? status,
    Value<int>? baseVersion,
    Value<String?>? conflictFilePath,
    Value<String?>? localContentSnapshot,
    Value<int>? rowid,
  }) {
    return MutationQueueCompanion(
      id: id ?? this.id,
      path: path ?? this.path,
      operation: operation ?? this.operation,
      timestamp: timestamp ?? this.timestamp,
      retryCount: retryCount ?? this.retryCount,
      status: status ?? this.status,
      baseVersion: baseVersion ?? this.baseVersion,
      conflictFilePath: conflictFilePath ?? this.conflictFilePath,
      localContentSnapshot: localContentSnapshot ?? this.localContentSnapshot,
      rowid: rowid ?? this.rowid,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (id.present) {
      map['id'] = Variable<String>(id.value);
    }
    if (path.present) {
      map['path'] = Variable<String>(path.value);
    }
    if (operation.present) {
      map['operation'] = Variable<String>(operation.value);
    }
    if (timestamp.present) {
      map['timestamp'] = Variable<String>(timestamp.value);
    }
    if (retryCount.present) {
      map['retry_count'] = Variable<int>(retryCount.value);
    }
    if (status.present) {
      map['status'] = Variable<String>(status.value);
    }
    if (baseVersion.present) {
      map['base_version'] = Variable<int>(baseVersion.value);
    }
    if (conflictFilePath.present) {
      map['conflict_file_path'] = Variable<String>(conflictFilePath.value);
    }
    if (localContentSnapshot.present) {
      map['local_content_snapshot'] = Variable<String>(
        localContentSnapshot.value,
      );
    }
    if (rowid.present) {
      map['rowid'] = Variable<int>(rowid.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('MutationQueueCompanion(')
          ..write('id: $id, ')
          ..write('path: $path, ')
          ..write('operation: $operation, ')
          ..write('timestamp: $timestamp, ')
          ..write('retryCount: $retryCount, ')
          ..write('status: $status, ')
          ..write('baseVersion: $baseVersion, ')
          ..write('conflictFilePath: $conflictFilePath, ')
          ..write('localContentSnapshot: $localContentSnapshot, ')
          ..write('rowid: $rowid')
          ..write(')'))
        .toString();
  }
}

class $ChatMessagesTable extends ChatMessages
    with TableInfo<$ChatMessagesTable, ChatMessage> {
  @override
  final GeneratedDatabase attachedDatabase;
  final String? _alias;
  $ChatMessagesTable(this.attachedDatabase, [this._alias]);
  static const VerificationMeta _idMeta = const VerificationMeta('id');
  @override
  late final GeneratedColumn<int> id = GeneratedColumn<int>(
    'id',
    aliasedName,
    false,
    hasAutoIncrement: true,
    type: DriftSqlType.int,
    requiredDuringInsert: false,
    defaultConstraints: GeneratedColumn.constraintIsAlways(
      'PRIMARY KEY AUTOINCREMENT',
    ),
  );
  static const VerificationMeta _queryMeta = const VerificationMeta('query');
  @override
  late final GeneratedColumn<String> query = GeneratedColumn<String>(
    'query',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _responseMeta = const VerificationMeta(
    'response',
  );
  @override
  late final GeneratedColumn<String> response = GeneratedColumn<String>(
    'response',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  static const VerificationMeta _sourcesMeta = const VerificationMeta(
    'sources',
  );
  @override
  late final GeneratedColumn<String> sources = GeneratedColumn<String>(
    'sources',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _attachmentsMeta = const VerificationMeta(
    'attachments',
  );
  @override
  late final GeneratedColumn<String> attachments = GeneratedColumn<String>(
    'attachments',
    aliasedName,
    true,
    type: DriftSqlType.string,
    requiredDuringInsert: false,
  );
  static const VerificationMeta _timestampMeta = const VerificationMeta(
    'timestamp',
  );
  @override
  late final GeneratedColumn<String> timestamp = GeneratedColumn<String>(
    'timestamp',
    aliasedName,
    false,
    type: DriftSqlType.string,
    requiredDuringInsert: true,
  );
  @override
  List<GeneratedColumn> get $columns => [
    id,
    query,
    response,
    sources,
    attachments,
    timestamp,
  ];
  @override
  String get aliasedName => _alias ?? actualTableName;
  @override
  String get actualTableName => $name;
  static const String $name = 'chat_messages';
  @override
  VerificationContext validateIntegrity(
    Insertable<ChatMessage> instance, {
    bool isInserting = false,
  }) {
    final context = VerificationContext();
    final data = instance.toColumns(true);
    if (data.containsKey('id')) {
      context.handle(_idMeta, id.isAcceptableOrUnknown(data['id']!, _idMeta));
    }
    if (data.containsKey('query')) {
      context.handle(
        _queryMeta,
        query.isAcceptableOrUnknown(data['query']!, _queryMeta),
      );
    } else if (isInserting) {
      context.missing(_queryMeta);
    }
    if (data.containsKey('response')) {
      context.handle(
        _responseMeta,
        response.isAcceptableOrUnknown(data['response']!, _responseMeta),
      );
    } else if (isInserting) {
      context.missing(_responseMeta);
    }
    if (data.containsKey('sources')) {
      context.handle(
        _sourcesMeta,
        sources.isAcceptableOrUnknown(data['sources']!, _sourcesMeta),
      );
    }
    if (data.containsKey('attachments')) {
      context.handle(
        _attachmentsMeta,
        attachments.isAcceptableOrUnknown(
          data['attachments']!,
          _attachmentsMeta,
        ),
      );
    }
    if (data.containsKey('timestamp')) {
      context.handle(
        _timestampMeta,
        timestamp.isAcceptableOrUnknown(data['timestamp']!, _timestampMeta),
      );
    } else if (isInserting) {
      context.missing(_timestampMeta);
    }
    return context;
  }

  @override
  Set<GeneratedColumn> get $primaryKey => {id};
  @override
  ChatMessage map(Map<String, dynamic> data, {String? tablePrefix}) {
    final effectivePrefix = tablePrefix != null ? '$tablePrefix.' : '';
    return ChatMessage(
      id: attachedDatabase.typeMapping.read(
        DriftSqlType.int,
        data['${effectivePrefix}id'],
      )!,
      query: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}query'],
      )!,
      response: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}response'],
      )!,
      sources: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}sources'],
      ),
      attachments: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}attachments'],
      ),
      timestamp: attachedDatabase.typeMapping.read(
        DriftSqlType.string,
        data['${effectivePrefix}timestamp'],
      )!,
    );
  }

  @override
  $ChatMessagesTable createAlias(String alias) {
    return $ChatMessagesTable(attachedDatabase, alias);
  }
}

class ChatMessage extends DataClass implements Insertable<ChatMessage> {
  final int id;
  final String query;
  final String response;
  final String? sources;
  final String? attachments;
  final String timestamp;
  const ChatMessage({
    required this.id,
    required this.query,
    required this.response,
    this.sources,
    this.attachments,
    required this.timestamp,
  });
  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    map['id'] = Variable<int>(id);
    map['query'] = Variable<String>(query);
    map['response'] = Variable<String>(response);
    if (!nullToAbsent || sources != null) {
      map['sources'] = Variable<String>(sources);
    }
    if (!nullToAbsent || attachments != null) {
      map['attachments'] = Variable<String>(attachments);
    }
    map['timestamp'] = Variable<String>(timestamp);
    return map;
  }

  ChatMessagesCompanion toCompanion(bool nullToAbsent) {
    return ChatMessagesCompanion(
      id: Value(id),
      query: Value(query),
      response: Value(response),
      sources: sources == null && nullToAbsent
          ? const Value.absent()
          : Value(sources),
      attachments: attachments == null && nullToAbsent
          ? const Value.absent()
          : Value(attachments),
      timestamp: Value(timestamp),
    );
  }

  factory ChatMessage.fromJson(
    Map<String, dynamic> json, {
    ValueSerializer? serializer,
  }) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return ChatMessage(
      id: serializer.fromJson<int>(json['id']),
      query: serializer.fromJson<String>(json['query']),
      response: serializer.fromJson<String>(json['response']),
      sources: serializer.fromJson<String?>(json['sources']),
      attachments: serializer.fromJson<String?>(json['attachments']),
      timestamp: serializer.fromJson<String>(json['timestamp']),
    );
  }
  @override
  Map<String, dynamic> toJson({ValueSerializer? serializer}) {
    serializer ??= driftRuntimeOptions.defaultSerializer;
    return <String, dynamic>{
      'id': serializer.toJson<int>(id),
      'query': serializer.toJson<String>(query),
      'response': serializer.toJson<String>(response),
      'sources': serializer.toJson<String?>(sources),
      'attachments': serializer.toJson<String?>(attachments),
      'timestamp': serializer.toJson<String>(timestamp),
    };
  }

  ChatMessage copyWith({
    int? id,
    String? query,
    String? response,
    Value<String?> sources = const Value.absent(),
    Value<String?> attachments = const Value.absent(),
    String? timestamp,
  }) => ChatMessage(
    id: id ?? this.id,
    query: query ?? this.query,
    response: response ?? this.response,
    sources: sources.present ? sources.value : this.sources,
    attachments: attachments.present ? attachments.value : this.attachments,
    timestamp: timestamp ?? this.timestamp,
  );
  ChatMessage copyWithCompanion(ChatMessagesCompanion data) {
    return ChatMessage(
      id: data.id.present ? data.id.value : this.id,
      query: data.query.present ? data.query.value : this.query,
      response: data.response.present ? data.response.value : this.response,
      sources: data.sources.present ? data.sources.value : this.sources,
      attachments: data.attachments.present
          ? data.attachments.value
          : this.attachments,
      timestamp: data.timestamp.present ? data.timestamp.value : this.timestamp,
    );
  }

  @override
  String toString() {
    return (StringBuffer('ChatMessage(')
          ..write('id: $id, ')
          ..write('query: $query, ')
          ..write('response: $response, ')
          ..write('sources: $sources, ')
          ..write('attachments: $attachments, ')
          ..write('timestamp: $timestamp')
          ..write(')'))
        .toString();
  }

  @override
  int get hashCode =>
      Object.hash(id, query, response, sources, attachments, timestamp);
  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is ChatMessage &&
          other.id == this.id &&
          other.query == this.query &&
          other.response == this.response &&
          other.sources == this.sources &&
          other.attachments == this.attachments &&
          other.timestamp == this.timestamp);
}

class ChatMessagesCompanion extends UpdateCompanion<ChatMessage> {
  final Value<int> id;
  final Value<String> query;
  final Value<String> response;
  final Value<String?> sources;
  final Value<String?> attachments;
  final Value<String> timestamp;
  const ChatMessagesCompanion({
    this.id = const Value.absent(),
    this.query = const Value.absent(),
    this.response = const Value.absent(),
    this.sources = const Value.absent(),
    this.attachments = const Value.absent(),
    this.timestamp = const Value.absent(),
  });
  ChatMessagesCompanion.insert({
    this.id = const Value.absent(),
    required String query,
    required String response,
    this.sources = const Value.absent(),
    this.attachments = const Value.absent(),
    required String timestamp,
  }) : query = Value(query),
       response = Value(response),
       timestamp = Value(timestamp);
  static Insertable<ChatMessage> custom({
    Expression<int>? id,
    Expression<String>? query,
    Expression<String>? response,
    Expression<String>? sources,
    Expression<String>? attachments,
    Expression<String>? timestamp,
  }) {
    return RawValuesInsertable({
      if (id != null) 'id': id,
      if (query != null) 'query': query,
      if (response != null) 'response': response,
      if (sources != null) 'sources': sources,
      if (attachments != null) 'attachments': attachments,
      if (timestamp != null) 'timestamp': timestamp,
    });
  }

  ChatMessagesCompanion copyWith({
    Value<int>? id,
    Value<String>? query,
    Value<String>? response,
    Value<String?>? sources,
    Value<String?>? attachments,
    Value<String>? timestamp,
  }) {
    return ChatMessagesCompanion(
      id: id ?? this.id,
      query: query ?? this.query,
      response: response ?? this.response,
      sources: sources ?? this.sources,
      attachments: attachments ?? this.attachments,
      timestamp: timestamp ?? this.timestamp,
    );
  }

  @override
  Map<String, Expression> toColumns(bool nullToAbsent) {
    final map = <String, Expression>{};
    if (id.present) {
      map['id'] = Variable<int>(id.value);
    }
    if (query.present) {
      map['query'] = Variable<String>(query.value);
    }
    if (response.present) {
      map['response'] = Variable<String>(response.value);
    }
    if (sources.present) {
      map['sources'] = Variable<String>(sources.value);
    }
    if (attachments.present) {
      map['attachments'] = Variable<String>(attachments.value);
    }
    if (timestamp.present) {
      map['timestamp'] = Variable<String>(timestamp.value);
    }
    return map;
  }

  @override
  String toString() {
    return (StringBuffer('ChatMessagesCompanion(')
          ..write('id: $id, ')
          ..write('query: $query, ')
          ..write('response: $response, ')
          ..write('sources: $sources, ')
          ..write('attachments: $attachments, ')
          ..write('timestamp: $timestamp')
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
  late final $MutationQueueTable mutationQueue = $MutationQueueTable(this);
  late final $ChatMessagesTable chatMessages = $ChatMessagesTable(this);
  @override
  Iterable<TableInfo<Table, Object?>> get allTables =>
      allSchemaEntities.whereType<TableInfo<Table, Object?>>();
  @override
  List<DatabaseSchemaEntity> get allSchemaEntities => [
    fileCacheEntries,
    mutationQueue,
    chatMessages,
  ];
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
      Value<int> serverVersion,
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
      Value<int> serverVersion,
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

  ColumnFilters<int> get serverVersion => $composableBuilder(
    column: $table.serverVersion,
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

  ColumnOrderings<int> get serverVersion => $composableBuilder(
    column: $table.serverVersion,
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

  GeneratedColumn<int> get serverVersion => $composableBuilder(
    column: $table.serverVersion,
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
                Value<int> serverVersion = const Value.absent(),
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
                serverVersion: serverVersion,
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
                Value<int> serverVersion = const Value.absent(),
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
                serverVersion: serverVersion,
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
typedef $$MutationQueueTableCreateCompanionBuilder =
    MutationQueueCompanion Function({
      required String id,
      required String path,
      required String operation,
      required String timestamp,
      Value<int> retryCount,
      required String status,
      required int baseVersion,
      Value<String?> conflictFilePath,
      Value<String?> localContentSnapshot,
      Value<int> rowid,
    });
typedef $$MutationQueueTableUpdateCompanionBuilder =
    MutationQueueCompanion Function({
      Value<String> id,
      Value<String> path,
      Value<String> operation,
      Value<String> timestamp,
      Value<int> retryCount,
      Value<String> status,
      Value<int> baseVersion,
      Value<String?> conflictFilePath,
      Value<String?> localContentSnapshot,
      Value<int> rowid,
    });

class $$MutationQueueTableFilterComposer
    extends Composer<_$AppDatabase, $MutationQueueTable> {
  $$MutationQueueTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get path => $composableBuilder(
    column: $table.path,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get operation => $composableBuilder(
    column: $table.operation,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get timestamp => $composableBuilder(
    column: $table.timestamp,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get retryCount => $composableBuilder(
    column: $table.retryCount,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get status => $composableBuilder(
    column: $table.status,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<int> get baseVersion => $composableBuilder(
    column: $table.baseVersion,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get conflictFilePath => $composableBuilder(
    column: $table.conflictFilePath,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get localContentSnapshot => $composableBuilder(
    column: $table.localContentSnapshot,
    builder: (column) => ColumnFilters(column),
  );
}

class $$MutationQueueTableOrderingComposer
    extends Composer<_$AppDatabase, $MutationQueueTable> {
  $$MutationQueueTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<String> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get path => $composableBuilder(
    column: $table.path,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get operation => $composableBuilder(
    column: $table.operation,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get timestamp => $composableBuilder(
    column: $table.timestamp,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get retryCount => $composableBuilder(
    column: $table.retryCount,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get status => $composableBuilder(
    column: $table.status,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<int> get baseVersion => $composableBuilder(
    column: $table.baseVersion,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get conflictFilePath => $composableBuilder(
    column: $table.conflictFilePath,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get localContentSnapshot => $composableBuilder(
    column: $table.localContentSnapshot,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$MutationQueueTableAnnotationComposer
    extends Composer<_$AppDatabase, $MutationQueueTable> {
  $$MutationQueueTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<String> get id =>
      $composableBuilder(column: $table.id, builder: (column) => column);

  GeneratedColumn<String> get path =>
      $composableBuilder(column: $table.path, builder: (column) => column);

  GeneratedColumn<String> get operation =>
      $composableBuilder(column: $table.operation, builder: (column) => column);

  GeneratedColumn<String> get timestamp =>
      $composableBuilder(column: $table.timestamp, builder: (column) => column);

  GeneratedColumn<int> get retryCount => $composableBuilder(
    column: $table.retryCount,
    builder: (column) => column,
  );

  GeneratedColumn<String> get status =>
      $composableBuilder(column: $table.status, builder: (column) => column);

  GeneratedColumn<int> get baseVersion => $composableBuilder(
    column: $table.baseVersion,
    builder: (column) => column,
  );

  GeneratedColumn<String> get conflictFilePath => $composableBuilder(
    column: $table.conflictFilePath,
    builder: (column) => column,
  );

  GeneratedColumn<String> get localContentSnapshot => $composableBuilder(
    column: $table.localContentSnapshot,
    builder: (column) => column,
  );
}

class $$MutationQueueTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $MutationQueueTable,
          MutationQueueData,
          $$MutationQueueTableFilterComposer,
          $$MutationQueueTableOrderingComposer,
          $$MutationQueueTableAnnotationComposer,
          $$MutationQueueTableCreateCompanionBuilder,
          $$MutationQueueTableUpdateCompanionBuilder,
          (
            MutationQueueData,
            BaseReferences<
              _$AppDatabase,
              $MutationQueueTable,
              MutationQueueData
            >,
          ),
          MutationQueueData,
          PrefetchHooks Function()
        > {
  $$MutationQueueTableTableManager(_$AppDatabase db, $MutationQueueTable table)
    : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$MutationQueueTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$MutationQueueTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$MutationQueueTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<String> id = const Value.absent(),
                Value<String> path = const Value.absent(),
                Value<String> operation = const Value.absent(),
                Value<String> timestamp = const Value.absent(),
                Value<int> retryCount = const Value.absent(),
                Value<String> status = const Value.absent(),
                Value<int> baseVersion = const Value.absent(),
                Value<String?> conflictFilePath = const Value.absent(),
                Value<String?> localContentSnapshot = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => MutationQueueCompanion(
                id: id,
                path: path,
                operation: operation,
                timestamp: timestamp,
                retryCount: retryCount,
                status: status,
                baseVersion: baseVersion,
                conflictFilePath: conflictFilePath,
                localContentSnapshot: localContentSnapshot,
                rowid: rowid,
              ),
          createCompanionCallback:
              ({
                required String id,
                required String path,
                required String operation,
                required String timestamp,
                Value<int> retryCount = const Value.absent(),
                required String status,
                required int baseVersion,
                Value<String?> conflictFilePath = const Value.absent(),
                Value<String?> localContentSnapshot = const Value.absent(),
                Value<int> rowid = const Value.absent(),
              }) => MutationQueueCompanion.insert(
                id: id,
                path: path,
                operation: operation,
                timestamp: timestamp,
                retryCount: retryCount,
                status: status,
                baseVersion: baseVersion,
                conflictFilePath: conflictFilePath,
                localContentSnapshot: localContentSnapshot,
                rowid: rowid,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$MutationQueueTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $MutationQueueTable,
      MutationQueueData,
      $$MutationQueueTableFilterComposer,
      $$MutationQueueTableOrderingComposer,
      $$MutationQueueTableAnnotationComposer,
      $$MutationQueueTableCreateCompanionBuilder,
      $$MutationQueueTableUpdateCompanionBuilder,
      (
        MutationQueueData,
        BaseReferences<_$AppDatabase, $MutationQueueTable, MutationQueueData>,
      ),
      MutationQueueData,
      PrefetchHooks Function()
    >;
typedef $$ChatMessagesTableCreateCompanionBuilder =
    ChatMessagesCompanion Function({
      Value<int> id,
      required String query,
      required String response,
      Value<String?> sources,
      Value<String?> attachments,
      required String timestamp,
    });
typedef $$ChatMessagesTableUpdateCompanionBuilder =
    ChatMessagesCompanion Function({
      Value<int> id,
      Value<String> query,
      Value<String> response,
      Value<String?> sources,
      Value<String?> attachments,
      Value<String> timestamp,
    });

class $$ChatMessagesTableFilterComposer
    extends Composer<_$AppDatabase, $ChatMessagesTable> {
  $$ChatMessagesTableFilterComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnFilters<int> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get query => $composableBuilder(
    column: $table.query,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get response => $composableBuilder(
    column: $table.response,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get sources => $composableBuilder(
    column: $table.sources,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get attachments => $composableBuilder(
    column: $table.attachments,
    builder: (column) => ColumnFilters(column),
  );

  ColumnFilters<String> get timestamp => $composableBuilder(
    column: $table.timestamp,
    builder: (column) => ColumnFilters(column),
  );
}

class $$ChatMessagesTableOrderingComposer
    extends Composer<_$AppDatabase, $ChatMessagesTable> {
  $$ChatMessagesTableOrderingComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  ColumnOrderings<int> get id => $composableBuilder(
    column: $table.id,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get query => $composableBuilder(
    column: $table.query,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get response => $composableBuilder(
    column: $table.response,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get sources => $composableBuilder(
    column: $table.sources,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get attachments => $composableBuilder(
    column: $table.attachments,
    builder: (column) => ColumnOrderings(column),
  );

  ColumnOrderings<String> get timestamp => $composableBuilder(
    column: $table.timestamp,
    builder: (column) => ColumnOrderings(column),
  );
}

class $$ChatMessagesTableAnnotationComposer
    extends Composer<_$AppDatabase, $ChatMessagesTable> {
  $$ChatMessagesTableAnnotationComposer({
    required super.$db,
    required super.$table,
    super.joinBuilder,
    super.$addJoinBuilderToRootComposer,
    super.$removeJoinBuilderFromRootComposer,
  });
  GeneratedColumn<int> get id =>
      $composableBuilder(column: $table.id, builder: (column) => column);

  GeneratedColumn<String> get query =>
      $composableBuilder(column: $table.query, builder: (column) => column);

  GeneratedColumn<String> get response =>
      $composableBuilder(column: $table.response, builder: (column) => column);

  GeneratedColumn<String> get sources =>
      $composableBuilder(column: $table.sources, builder: (column) => column);

  GeneratedColumn<String> get attachments => $composableBuilder(
    column: $table.attachments,
    builder: (column) => column,
  );

  GeneratedColumn<String> get timestamp =>
      $composableBuilder(column: $table.timestamp, builder: (column) => column);
}

class $$ChatMessagesTableTableManager
    extends
        RootTableManager<
          _$AppDatabase,
          $ChatMessagesTable,
          ChatMessage,
          $$ChatMessagesTableFilterComposer,
          $$ChatMessagesTableOrderingComposer,
          $$ChatMessagesTableAnnotationComposer,
          $$ChatMessagesTableCreateCompanionBuilder,
          $$ChatMessagesTableUpdateCompanionBuilder,
          (
            ChatMessage,
            BaseReferences<_$AppDatabase, $ChatMessagesTable, ChatMessage>,
          ),
          ChatMessage,
          PrefetchHooks Function()
        > {
  $$ChatMessagesTableTableManager(_$AppDatabase db, $ChatMessagesTable table)
    : super(
        TableManagerState(
          db: db,
          table: table,
          createFilteringComposer: () =>
              $$ChatMessagesTableFilterComposer($db: db, $table: table),
          createOrderingComposer: () =>
              $$ChatMessagesTableOrderingComposer($db: db, $table: table),
          createComputedFieldComposer: () =>
              $$ChatMessagesTableAnnotationComposer($db: db, $table: table),
          updateCompanionCallback:
              ({
                Value<int> id = const Value.absent(),
                Value<String> query = const Value.absent(),
                Value<String> response = const Value.absent(),
                Value<String?> sources = const Value.absent(),
                Value<String?> attachments = const Value.absent(),
                Value<String> timestamp = const Value.absent(),
              }) => ChatMessagesCompanion(
                id: id,
                query: query,
                response: response,
                sources: sources,
                attachments: attachments,
                timestamp: timestamp,
              ),
          createCompanionCallback:
              ({
                Value<int> id = const Value.absent(),
                required String query,
                required String response,
                Value<String?> sources = const Value.absent(),
                Value<String?> attachments = const Value.absent(),
                required String timestamp,
              }) => ChatMessagesCompanion.insert(
                id: id,
                query: query,
                response: response,
                sources: sources,
                attachments: attachments,
                timestamp: timestamp,
              ),
          withReferenceMapper: (p0) => p0
              .map((e) => (e.readTable(table), BaseReferences(db, table, e)))
              .toList(),
          prefetchHooksCallback: null,
        ),
      );
}

typedef $$ChatMessagesTableProcessedTableManager =
    ProcessedTableManager<
      _$AppDatabase,
      $ChatMessagesTable,
      ChatMessage,
      $$ChatMessagesTableFilterComposer,
      $$ChatMessagesTableOrderingComposer,
      $$ChatMessagesTableAnnotationComposer,
      $$ChatMessagesTableCreateCompanionBuilder,
      $$ChatMessagesTableUpdateCompanionBuilder,
      (
        ChatMessage,
        BaseReferences<_$AppDatabase, $ChatMessagesTable, ChatMessage>,
      ),
      ChatMessage,
      PrefetchHooks Function()
    >;

class $AppDatabaseManager {
  final _$AppDatabase _db;
  $AppDatabaseManager(this._db);
  $$FileCacheEntriesTableTableManager get fileCacheEntries =>
      $$FileCacheEntriesTableTableManager(_db, _db.fileCacheEntries);
  $$MutationQueueTableTableManager get mutationQueue =>
      $$MutationQueueTableTableManager(_db, _db.mutationQueue);
  $$ChatMessagesTableTableManager get chatMessages =>
      $$ChatMessagesTableTableManager(_db, _db.chatMessages);
}
