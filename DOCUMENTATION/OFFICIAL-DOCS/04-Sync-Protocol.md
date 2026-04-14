# 04 — Sync Protocol

## Scope

Defines the selective, bi-directional file synchronization protocol between `jv-app` (mobile client) and `jv-api` (server). Covers manifest exchange, conflict resolution, transfer mechanics, and edge cases.

---

## Design Goals

1. **Selective**: Users choose which folders to sync
2. **Bi-directional**: Changes flow in both directions
3. **Offline-tolerant**: Queue changes when disconnected; replay on reconnect
4. **Conflict-safe**: No silent data loss; conflicts are surfaced to the user
5. **Bandwidth-efficient**: Only transfer changed files; use hash comparison
6. **Resumable**: Interrupted syncs can resume without re-transferring completed files

---

## Sync Model

### Per-File State

Each file tracked with this metadata:

| Field | Type | Description |
|---|---|---|
| `path` | string | Relative path from vault root |
| `content_hash` | string | SHA-256 of file content |
| `last_modified` | ISO 8601 | Last modification timestamp |
| `size_bytes` | integer | File size |
| `is_deleted` | boolean | Tombstone for deletions |
| `sync_version` | integer | Monotonically increasing counter per file |

### Sync Direction per Folder

| Mode | Behavior |
|---|---|
| `both` | Full bi-directional sync (default) |
| `pull_only` | Server → mobile only (read-only mirror) |
| `push_only` | Mobile → server only (capture/upload) |
| `disabled` | No sync for this folder |

---

## Protocol Steps

### Phase 1: Manifest Exchange

```
Client                                          Server
  │                                                │
  │  POST /sync/manifest                           │
  │  Body: {                                       │
  │    folders: ["Personal", "Work"],              │
  │    manifest: [                                 │
  │      {path, content_hash, last_modified,       │
  │       sync_version, is_deleted}                │
  │    ]                                           │
  │  }                                             │
  ├───────────────────────────────────────────────►│
  │                                                │
  │  Response: {                                   │
  │    to_push: [{path, ...}],   // client→server  │
  │    to_pull: [{path, ...}],   // server→client  │
  │    conflicts: [{path, client_hash,             │
  │                 server_hash, ...}]             │
  │  }                                             │
  │◄───────────────────────────────────────────────┤
```

### Phase 2: Conflict Resolution

For each entry in `conflicts`:

```
Decision Matrix:
┌──────────────────────┬─────────────────────┬──────────────────────┐
│ Scenario             │ Condition           │ Resolution           │
├──────────────────────┼─────────────────────┼──────────────────────┤
│ Only client changed  │ server hash matches │ Push client version  │
│                      │ client's last_synced│                      │
├──────────────────────┼─────────────────────┼──────────────────────┤
│ Only server changed  │ client hash matches │ Pull server version  │
│                      │ server's last_synced│                      │
├──────────────────────┼─────────────────────┼──────────────────────┤
│ Both changed         │ hashes differ from  │ CONFLICT             │
│ (true conflict)      │ last_synced hash    │                      │
├──────────────────────┼─────────────────────┼──────────────────────┤
│ Identical changes    │ client_hash ==      │ No action needed     │
│                      │ server_hash         │                      │
├──────────────────────┼─────────────────────┼──────────────────────┤
│ Client deleted,      │ server modified     │ CONFLICT (keep both) │
│ server modified      │ after delete        │                      │
├──────────────────────┼─────────────────────┼──────────────────────┤
│ Client modified,     │ client modified     │ CONFLICT (keep both) │
│ server deleted       │ after delete        │                      │
└──────────────────────┴─────────────────────┴──────────────────────┘
```

#### Conflict Resolution Strategy

1. **Server version preserved** at original path
2. **Client version saved** locally in SQLite `MutationQueue` via `localContentSnapshot`
3. Conflict badge appears in the user's file browser leading to a 2-step resolution UI (Compare/Edit)
4. User manually resolves by keeping one and deleting the other
5. Mobile app shows conflict badge on affected files

### Phase 3: File Transfer — Push

```
Client                                          Server
  │                                                │
  │  POST /sync/push                               │
  │  Body: multipart/form-data                     │
  │    - manifest_entry (JSON)                     │
  │    - file_content (binary)                     │
  ├───────────────────────────────────────────────►│
  │                                                │
  │  Response: {                                   │
  │    accepted: [{path, new_sync_version}],       │
  │    rejected: [{path, reason}]                  │
  │  }                                             │
  │◄───────────────────────────────────────────────┤
```

### Phase 4: File Transfer — Pull

```
Client                                          Server
  │                                                │
  │  POST /sync/pull                               │
  │  Body: { paths: ["Personal/notes.md", ...] }   │
  ├───────────────────────────────────────────────►│
  │                                                │
  │  Response: multipart stream                    │
  │    - For each file: metadata header + content  │
  │◄───────────────────────────────────────────────┤
```

### Phase 5: Confirm

```
Client                                          Server
  │                                                │
  │  POST /sync/confirm                            │
  │  Body: {                                       │
  │    confirmed: [{path, sync_version}]           │
  │  }                                             │
  ├───────────────────────────────────────────────►│
  │                                                │
  │  Response: { status: "ok" }                    │
  │◄───────────────────────────────────────────────┤
```

> This final confirmation ensures both sides agree on what was synced. If the client crashes between pull and confirm, the next sync will re-pull those files.

---

## Hash Algorithm

- **Algorithm**: SHA-256
- **Input**: Raw file bytes
- **Representation**: Hex-encoded lowercase string
- **Prefix**: `sha256:` (e.g., `sha256:e3b0c44298fc1c149afb...`)
- **Empty file hash**: `sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`

---

## Tombstones (Deletion Tracking)

- When a file is deleted, a tombstone entry is created: `{path, is_deleted: true, timestamp}`
- Tombstones are exchanged during manifest comparison
- Tombstones expire after **30 days** (configurable)
- If a tombstone conflicts with a modification, the modification wins (file is kept)

---

## Bandwidth Optimization

| Technique | Description |
|---|---|
| Hash-only manifest | Initial exchange sends metadata only (no file content) |
| Delta skip | Files with matching hashes are skipped entirely |
| Compression | File content compressed with gzip during transfer |
| Batched transfer | Multiple small files sent in a single multipart request |
| Chunked transfer | Large files (>10MB) split into chunks for resumability |

---

## Sync Triggers

| Trigger | Behavior |
|---|---|
| App foreground | Auto-sync if server reachable and >5 min since last sync |
| Manual button | User taps "Sync Now" → immediate full sync |
| File save | Debounced push after 10 seconds of idle |
| Connectivity restored | Auto-sync queued changes |
| Background (Android) | Periodic WorkManager task (every 30 min when charging) |

---

## Failure Handling

| Failure | Handling |
|---|---|
| Network drop mid-sync | Resume from last confirmed file on next sync |
| Server rejects push (409) | Treat as conflict; apply conflict resolution |
| Hash mismatch after transfer | Re-transfer file; log warning |
| Client disk full | Abort pull; notify user; push still allowed |
| Server disk full | Server returns `503`; client retries later |
| Timeout on large file | Chunk-based retry; skip after 3 failures |

---

## Edge Cases

| Scenario | Handling |
|---|---|
| File renamed on both sides | Treated as delete + create; may result in duplicate |
| Folder deleted, files modified inside | Each file conflict resolved independently |
| Rapid successive edits | Only latest version synced (debounce window) |
| Clock skew between devices | Hash comparison is primary; timestamps are secondary |
| File moved to different folder | Detected as delete in old + create in new location |
| Sync-enabled folder reduced | Removed files deleted locally; server copy preserved |

---

## Security Considerations

- All sync traffic over HTTPS via Tailscale
- JWT token required for all sync endpoints
- File content integrity verified via SHA-256 hash after transfer
- No server-side file content caching outside `/JARVIS`
- Sync manifest does not contain file content (metadata only)

---

## Future Extensibility

- **Real-time sync**: WebSocket-based push notifications for instant sync
- **Partial file sync**: Sync only changed blocks within large files
- **Sync history**: Audit log of all sync operations
- **Multi-device sync**: Manifest comparison across N devices (not just client↔server)
