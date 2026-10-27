# 02 — Backend Specification

## Scope

Technical specification for `jv-api` — the Dockerized FastAPI backend that serves as the central gateway for all vault operations, sync coordination, and AI query routing.

---

## Technology Stack

| Component | Technology | Version (baseline) |
|---|---|---|
| Language | Python | 3.14+ |
| Framework | FastAPI | Latest stable |
| ASGI Server | Uvicorn | Latest stable |
| Container | Docker | 29.x |
| Orchestration | Docker Compose | v5.x |
| Config | Pydantic Settings + `.env` | — |

---

## Service Architecture

```
jv-api container (actual layout, verified 2026-09-24)
├── app/
│   ├── main.py              # App, lifespan (setup secret, daily backup loop), rate-limit + audit middleware
│   ├── config.py            # JARVIS_* settings
│   ├── dependencies.py      # get_current_device, require_admin, ensure_path_access
│   ├── errors.py            # VaultError / AuthError + handlers
│   ├── routers/
│   │   ├── health.py        # GET /health
│   │   ├── auth.py          # register, reconnect, refresh, revoke, devices, QR invite
│   │   ├── files.py         # /files CRUD, /upload, /download, /files/reset
│   │   ├── sync.py          # /sync/manifest, /sync/push, /sync/pull
│   │   ├── ask.py           # /ask/* proxy to jv-brain (NDJSON streams)
│   │   └── backup.py        # /backup list, export, download
│   ├── services/
│   │   ├── vault.py         # Filesystem ops over the vault
│   │   ├── sync.py          # Manifest build + diff (LOCKED — see 5-version-control.md)
│   │   ├── version_tracker.py  # Per-file versions in system/file_versions.db
│   │   ├── path_validator.py   # Traversal, hidden, reserved (system/), Secrets detection
│   │   ├── auth.py          # Device registry, JWT, invite tokens
│   │   ├── rate_limit.py    # Per-group request limits
│   │   ├── audit.py         # .system/audit.log (JSON lines, rotated)
│   │   └── backup.py        # Vault zips
│   └── models/              # Pydantic schemas (auth, file, sync)
├── tests/
├── Dockerfile
└── requirements.txt
```

> Secrets are encrypted/decrypted **on the device only** (`.jvs`, see 07); the server stores
> ciphertext through the normal file/sync endpoints. There is no `/secrets` router.

---

## API Endpoints

### Health & Status

| Method | Path | Description | Auth |
|---|---|---|---|
| `GET` | `/health` | Service health check | None |
| `GET` | `/status` | Vault stats (file count, disk usage) | JWT |

### File Operations (`/files`)

| Method | Path | Description | Auth |
|---|---|---|---|
| `GET` | `/files` | List directory contents | JWT |
| `GET` | `/files/{path:path}` | Read file content or metadata | JWT |
| `POST` | `/files/{path:path}` | Create file or directory | JWT |
| `PUT` | `/files/{path:path}` | Update file content | JWT |
| `DELETE` | `/files/{path:path}` | Delete file or directory | JWT |
| `POST` | `/files/upload` | Upload media/binary file (multipart) | JWT |
| `GET` | `/files/download/{path:path}` | Download file (streaming) | JWT |

#### Path Safety Rules
- All paths are resolved relative to `/JARVIS` mount
- Path traversal (`..`) is **rejected** with `400 Bad Request`
- Symlinks are **not followed**
- Maximum path depth: 10 levels
- Maximum filename length: 255 characters

#### File Metadata Response Schema
```json
{
  "path": "Personal/notes.md",
  "name": "notes.md",
  "type": "file",
  "size_bytes": 1024,
  "mime_type": "text/markdown",
  "last_modified": "2026-02-19T10:00:00Z",
  "content_hash": "sha256:abc123..."
}
```

### Sync Endpoints (`/sync`)

| Method | Path | Description | Auth |
|---|---|---|---|
| `POST` | `/sync/manifest` | Receive client manifest, return diff | JWT |
| `POST` | `/sync/push` | Client pushes changed files to server | JWT |
| `POST` | `/sync/pull` | Client pulls changed files from server | JWT |

> Detailed sync protocol defined in [04-Sync-Protocol.md](file:///b:/DEV/JARVIS/DOCUMENTATION/OFFICIAL-DOCS/04-Sync-Protocol.md)

### AI Endpoints (`/ask/*`)

All proxied to jv-brain (`server/app/routers/ask.py`); streams are NDJSON.

| Method | Path | Description |
|---|---|---|
| `POST` | `/ask/ai/query` | RAG answer (stream) |
| `POST` | `/ask/ai/agent` | Tool-calling agent chat (stream; stops at `approval_required`) |
| `GET` | `/ask/ai/tools` | Tool catalogue + risk classes for the permission UI |
| `GET` | `/ask/status`, `/ask/index-status` | AI health / indexer progress |
| `POST` | `/ask/reindex`, `/ask/refresh-fs-tree` | Trigger reindex / fs snapshot rebuild |
| `POST` | `/ask/extract-pdf` | PDF pages → Markdown |
| `POST` | `/ask/generate-files`, `/ask/generate-files/dry-run` | LLM file-scaffold manifest (mobile writes it) |
| `GET/DELETE` | `/ask/chat/sessions[/{id}]` | Server-side chat history |
| `POST` | `/ask/chat/sync` | Store a message pair in chat history |

> Detailed AI design in [05-AI-System-Design.md](05-AI-System-Design.md)

### Backup & Export

| Method | Path | Description | Auth |
|---|---|---|---|
| `GET` | `/backup` | List backups, newest first (`backup_id`, size, `created_at`) | JWT (admin) |
| `POST` | `/backup/export` | Zip the whole vault into `JARVIS_BACKUP_HOST_PATH` (synchronous; returns the backup) | JWT (admin) |
| `GET` | `/backup/download/{id}` | Download a backup zip | JWT (admin) |

Backups also run automatically every `JARVIS_BACKUP_INTERVAL_HOURS` (default 24, `0` = off),
keeping the newest `JARVIS_BACKUP_KEEP` (default 10). SQLite files (the version DB) are
snapshotted through the SQLite backup API, so a zip never holds a half-written DB.
"Admin" = a secrets-authorized device (see 06 §Permissions).

---

## Configuration

All configuration via environment variables (loaded from `.env`):

| Variable | Required | Default | Description |
|---|---|---|---|
| `JARVIS_VAULT_PATH` | Yes | `/data/JARVIS` | Vault mount path inside container |
| `JARVIS_JWT_SECRET` | Yes | — | Secret for signing JWT tokens |
| `JARVIS_JWT_EXPIRY_HOURS` | No | `720` (30 days) | Token expiry duration |
| `JARVIS_OLLAMA_URL` | No | `http://ollama:11434` | Ollama service URL |
| `JARVIS_VECTORDB_URL` | No | `http://vectordb:8000` | Vector DB connection |
| `JARVIS_LOG_LEVEL` | No | `INFO` | Logging level |
| `JARVIS_MAX_UPLOAD_MB` | No | `100` | Maximum upload file size |
| `JARVIS_CORS_ORIGINS` | No | `""` | Allowed CORS origins (empty = none) |

---

## Docker Configuration

### Dockerfile (multi-stage)
```dockerfile
# Build stage
FROM python:3.14-slim AS builder
WORKDIR /build
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Runtime stage
FROM python:3.14-slim
WORKDIR /app
COPY --from=builder /usr/local/lib/python3.14/site-packages /usr/local/lib/python3.14/site-packages
COPY app/ ./app/
EXPOSE 8000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
```

### docker-compose.yml (jv-api service)
```yaml
services:
  api:
    build: ./server
    container_name: jv-api
    ports:
      - "127.0.0.1:8000:8000"  # localhost only, Tailscale handles remote
    volumes:
      - ${JARVIS_HOST_PATH}:/data/JARVIS
    env_file: .env
    depends_on:
      - ollama
      - vectordb
    networks:
      - jarvis-internal
    restart: unless-stopped
```

> [!NOTE]
> Port `8000` is bound to `127.0.0.1` only. Remote access is exclusively through Tailscale's overlay network, which routes to the host's Tailscale IP.

---

## Error Handling

### Standard Error Response
```json
{
  "error": {
    "code": "FILE_NOT_FOUND",
    "message": "The requested file does not exist.",
    "path": "Personal/nonexistent.md"
  }
}
```

### Error Codes

| Code | HTTP Status | Description |
|---|---|---|
| `FILE_NOT_FOUND` | 404 | Requested path does not exist |
| `PATH_TRAVERSAL` | 400 | Path contains `..` or escapes vault |
| `FILE_EXISTS` | 409 | File already exists (on create) |
| `UPLOAD_TOO_LARGE` | 413 | File exceeds max upload size |
| `SYNC_CONFLICT` | 409 | Conflicting changes detected |
| `AUTH_INVALID` | 401 | Missing or invalid JWT |
| `AUTH_EXPIRED` | 401 | JWT token expired |
| `AI_UNAVAILABLE` | 503 | Ollama or VectorDB unreachable |
| `VAULT_READ_ONLY` | 503 | Disk full or permissions error |

---

## Security Considerations

1. **Input validation**: All file paths sanitized; reject traversal attempts
2. **Rate limiting**: Configurable per-endpoint rate limits (default: 60 req/min)
3. **CORS**: Disabled by default; explicitly allow only known origins
4. **Request size limits**: Enforced at ASGI level
5. **No shell execution**: File operations use Python stdlib only; no `subprocess` calls
6. **Logging**: All mutations logged with timestamp, token identity, and path
7. **Health endpoint unauthenticated**: Returns only `{"status": "ok"}` — no data leakage

---

## Edge Cases

| Scenario | Handling |
|---|---|
| File locked by OS | Retry with backoff; return `503` after 3 attempts |
| Unicode filenames | UTF-8 throughout; normalize to NFC form |
| Empty directories | Returned in listings with `type: "directory"` |
| Binary file reads | Return base64-encoded content or stream download |
| Concurrent writes | Last-write-wins with hash verification; sync layer handles conflicts |
| Very large files (>100MB) | Chunked upload/download; configurable limit |

---

## Future Extensibility

- **WebSocket support**: Real-time file change notifications
- **Versioning**: Git-backed file history (optional layer)
- **Webhooks**: Notify external systems on file changes
- **GraphQL**: Alternative query interface for complex metadata queries
