# 01 — System Architecture

## Scope

This document defines the complete system architecture for JARVIS — a private, portable, AI-powered personal knowledge operating system. It covers the high-level topology, module boundaries, data flow, and deployment model.

---

## Design Principles

| Principle | Enforcement |
|---|---|
| **File-first** | `/JARVIS` folder is the single source of truth for all user data |
| **Docker-first** | Every backend service runs inside containers; no host-installed runtimes in production |
| **Zero public exposure** | No open ports; all traffic flows over Tailscale mesh VPN |
| **Offline-first mobile** | Flutter app works without connectivity; syncs when server is reachable |
| **Portable** | Copy `/JARVIS` + Docker files to any machine → `docker compose up` |
| **Modular** | Each subsystem is independently deployable and testable |

---

## System Topology

```
┌───────────────────────────────────────────────────────────────┐
│                     HOST MACHINE (Windows)                     │
│                                                               │
│  ┌──────────────────── Docker Engine ──────────────────────┐  │
│  │                                                         │  │
│  │  ┌─────────────┐  ┌──────────┐  ┌──────────────────┐   │  │
│  │  │  jv-api     │  │ jv-brain │  │  Ollama          │   │  │
│  │  │  (FastAPI)  │◄─┤ (RAG     │──┤  (Local LLM)     │   │  │
│  │  │             │  │  Engine)  │  │                  │   │  │
│  │  └──────┬──────┘  └────┬─────┘  └──────────────────┘   │  │
│  │         │              │                                │  │
│  │         │    ┌─────────┴─────────┐                      │  │
│  │         │    │  Vector DB        │                      │  │
│  │         │    │  (Chroma/pgvector)│                      │  │
│  │         │    └───────────────────┘                      │  │
│  │         │                                               │  │
│  │    ┌────▼────────────────────────────┐                  │  │
│  │    │  /JARVIS  (bind-mounted volume) │                  │  │
│  │    │  ├── /Personal                  │                  │  │
│  │    │  ├── /Education                 │                  │  │
│  │    │  ├── /Work                      │                  │  │
│  │    │  ├── /Secrets (encrypted)       │                  │  │
│  │    │  ├── /Goals                     │                  │  │
│  │    │  ├── /Ideas                     │                  │  │
│  │    │  └── ...                        │                  │  │
│  │    └─────────────────────────────────┘                  │  │
│  └─────────────────────────────────────────────────────────┘  │
│                          │                                    │
│            Tailscale (100.x.y.z)                              │
│                          │                                    │
└──────────────────────────┼────────────────────────────────────┘
                           │
              ┌────────────▼────────────────┐
              │   jv-app (Flutter Mobile)   │
              │   ┌──────────────────────┐  │
              │   │ Local SQLite + Cache │  │
              │   │ Offline mirror       │  │
              │   │ Sync client          │  │
              │   └──────────────────────┘  │
              └─────────────────────────────┘
```

---

## Module Inventory

| Module ID | Name | Runtime | Responsibility |
|---|---|---|---|
| `jv-api` | Backend API | Docker (FastAPI/Python) | File CRUD, sync endpoints, orchestration |
| `jv-brain` | AI Engine | Docker (Python) | Document indexing, embedding, RAG retrieval |
| `jv-ollama` | LLM Runtime | Docker (Ollama) | Local inference via `llama3` or other models |
| `jv-vectordb` | Vector Store | Docker (Chroma or PostgreSQL+pgvector) | Embedding storage and similarity search |
| `jv-app` | Mobile Client | Flutter (Android) | File browsing, editing, offline cache, sync |
| `jv-sync` | Sync Engine | Shared library (used by jv-api + jv-app) | Bi-directional selective file synchronization |
| `jv-secrets` | Secrets Service | Integrated in jv-api | AES-256 encryption/decryption of `/Secrets` |
| `jv-sec` | Security Layer | Integrated in jv-api | JWT/token auth, Tailscale verification |

---

## Data Flow Overview

```
┌──────────┐    HTTPS/Tailscale    ┌─────────┐    fs read/write    ┌──────────┐
│ jv-app   │ ──────────────────► │ jv-api  │ ────────────────► │ /JARVIS  │
│ (mobile) │ ◄────────────────── │         │ ◄──────────────── │ (vault)  │
└──────────┘   JSON responses     └────┬────┘                  └──────────┘
                                       │
                                       │  POST /ask
                                       ▼
                                  ┌─────────┐       query       ┌───────────┐
                                  │jv-brain │ ────────────────► │ VectorDB  │
                                  │ (RAG)   │ ◄──────────────── │           │
                                  └────┬────┘   top-K results   └───────────┘
                                       │
                                       │  prompt + context
                                       ▼
                                  ┌──────────┐
                                  │ Ollama   │
                                  │ (LLM)   │
                                  └──────────┘
```

### Request Flow: File Operation
1. `jv-app` sends authenticated request to `jv-api` over Tailscale
2. `jv-api` validates JWT token
3. `jv-api` performs filesystem operation on `/JARVIS` mount
4. Response returned to `jv-app`

### Request Flow: AI Query
1. `jv-app` sends `POST /ask` with prompt (+ optional file attachments)
2. `jv-api` forwards to `jv-brain`
3. `jv-brain` embeds the query, queries vector DB for top-K relevant chunks
4. `jv-brain` constructs prompt with context, sends to Ollama
5. Ollama returns generated response
6. Response streamed back through `jv-api` to `jv-app`

### Request Flow: Sync
1. `jv-app` sends metadata manifest (path, hash, timestamp) to `jv-api /sync/push`
2. `jv-api` compares with server-side metadata
3. Conflicts resolved via timestamp + hash; unresolvable conflicts recorded in SQLite MutationQueue
4. Changed files transferred in both directions
5. Both sides update metadata

---

## Inter-Service Communication

| From | To | Protocol | Auth |
|---|---|---|---|
| jv-app → jv-api | HTTPS over Tailscale | JWT Bearer token |
| jv-api → jv-brain | Internal Docker network | None (container-only) |
| jv-brain → Ollama | Internal Docker network (HTTP :11434) | None (container-only) |
| jv-brain → VectorDB | Internal Docker network | None (container-only) |
| jv-api → /JARVIS | Bind mount (filesystem) | OS-level permissions |

> [!IMPORTANT]
> Only `jv-api` is reachable from outside the Docker network. All other services communicate solely over the internal Docker bridge network and are **not** exposed.

---

## Storage Architecture

### Primary Storage: `/JARVIS` (Bind Mount)
- Contains all user data as plain files (markdown, media, etc.)
- Mounted into `jv-api` container at runtime
- Human-readable, version-controllable, portable

### Docker Volumes (Ephemeral/Derived)
| Volume | Purpose | Recreatable? |
|---|---|---|
| `ollama-models` | Downloaded LLM model weights | Yes (re-download) |
| `vectordb-data` | Embedding index | Yes (re-index from /JARVIS) |
| `api-config` | Runtime config, JWT keys | Sensitive — back up |

### Mobile Local Storage
- SQLite database for metadata cache
- Local file mirror in app-private storage
- Encryption keys in platform secure storage (Android Keystore)

---

## Failure Handling

| Failure | Impact | Mitigation |
|---|---|---|
| Server offline | Mobile app works offline; AI unavailable | Offline queue; sync on reconnect |
| Ollama OOM | AI queries fail | Return graceful error; API continues serving files |
| VectorDB corrupt | AI retrieval degraded | Re-index from `/JARVIS` files (no data loss) |
| Sync conflict | Both sides modified same file | Stored in SQLite MutationQueue; user resolves in app |
| Disk full | Write operations fail | Health check endpoint monitors disk; alert in app |

---

## Future Extensibility

- **Desktop client**: Same sync protocol can power a Windows/macOS app
- **Additional LLMs**: Swap Ollama models via config; no architecture change
- **Web dashboard**: Optional read-only viewer behind Tailscale
- **Plugin system**: Structured file watchers for automation (e.g., auto-tag, daily digest)
- **Multi-user**: Vault per user with isolated tokens (not planned for MVP)

---

## Constraints Reiteration

1. **No public cloud dependencies** — all services run locally
2. **No public ports** — Tailscale mesh VPN only
3. **No host-installed production dependencies** — Docker containers only
4. **All data portable** — copy `/JARVIS` + `docker-compose.yml` to migrate
5. **Single-user system** — one vault owner, multiple devices
