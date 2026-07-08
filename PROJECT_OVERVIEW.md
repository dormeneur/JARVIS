# JARVIS — Project Overview & Onboarding Brief

> Written for a developer joining the project. Read this first, then `HOW_TO_RUN.md` to get it running.
> Deep-dive specs live in `DOCUMENTATION/OFFICIAL-DOCS/` (11 numbered design docs) — this is the map to them.

---

## 1. The Idea (why this exists)

JARVIS is a **private, portable, offline-first AI personal knowledge OS**. All of the owner's
personal data lives as **plain files (mostly markdown) in one vault folder** (`/JARVIS`). That
folder — not a database — is the single source of truth. Around it we built:

- A **Dockerized backend** that exposes the vault over a secure API.
- A **Flutter mobile app** that mirrors the vault locally, works fully offline, and syncs both ways.
- A **local AI (RAG) layer** that indexes the vault and answers questions grounded in your own files —
  using Ollama, so **no data ever leaves your machines**.
- **Tailscale** (mesh VPN) as the only network path. No public ports, no cloud, ever.

Portability is a core promise: copy the vault folder + repo to any machine, `docker compose up`,
and everything works again.

## 2. Architecture in one picture

```
Host machine (Windows or Mac)
├── Docker (compose, network "jarvis-internal")
│     ├── jv-api      FastAPI, port 8000  ← ONLY host-exposed service
│     ├── jv-brain    FastAPI, port 8001  ← RAG engine (internal only)
│     ├── jv-chromadb ChromaDB 0.5.23     ← vector store (internal only)
│     └── jv-ollama   Ollama, port 11434  ← LLM runtime (internal only)
│           ├── llama3:8b            ← answer generation
│           └── nomic-embed-text     ← 768-dim embeddings
├── B:\JARVIS (vault folder)               ← bind-mounted as /data/JARVIS
└── Tailscale (100.x.y.z)  ←──────────────  Flutter app on Android phone
                                             (Drift/SQLite offline mirror)
```

- The phone talks **only to jv-api** (JWT auth). jv-api proxies AI calls to jv-brain.
- jv-brain talks to jv-ollama (`http://ollama:11434`) and ChromaDB, all inside the Docker network.
- Everything AI streams as **NDJSON** through the chain: Ollama → brain → api → mobile.

## 3. Repo layout

| Path | What it is |
|---|---|
| `server/` | jv-api — FastAPI: auth, file CRUD, sync, AI proxy. Tests in `server/tests/`. |
| `brain/` | jv-brain — RAG pipeline: loader → chunker → embedder → ChromaDB → retriever → Ollama. Tests in `brain/tests/`. |
| `mobile/` | Flutter app (Riverpod + Drift + Dio). Features: explorer, editor, viewer, sync, chat, secrets, auth, settings. |
| `docker-compose.yml`, `.env` | The whole backend stack. `.env` from `.env.template` (never committed). |
| `DOCUMENTATION/OFFICIAL-DOCS/` | Design specs 01–11 + `walkthrough.md` (historical Phase 3 handover). |
| `DOCUMENTATION/AI-AGENT-DOCS/` | Context docs for AI coding agents + debugging postmortems. `5-version-control.md` = **the sync bible**. |
| `scripts/autostart_jarvis.bat` | Windows autostart for the stack. |

## 4. What is DONE ✅

### Phase 1 — Core backend (complete, stable)
- File CRUD (`/files/*`), upload/download with streaming, path-traversal protection.
- Health endpoint, Pydantic settings config (`JARVIS_` env prefix), OpenAPI docs at `/docs`.

### Phase 2 — Sync engine (complete, **LOCKED — do not casually modify**)
- **Git-style per-file versioned sync** with optimistic concurrency (`baseVersion` vs `serverVersion`).
- 4 deterministic phases: push pending mutations → manifest diff → push missing → pull missing.
- Conflicts never overwrite silently: they become `MutationQueue` rows (SQLite, mobile side) and are
  resolved per-file in a 2-step UI (Compare → Edit/Keep Local/Accept Remote).
- Selective per-folder sync, offline mutation queue, replay on reconnect.
- Read `DOCUMENTATION/AI-AGENT-DOCS/5-version-control.md` before touching ANY sync code.
  Locked files: `mobile/.../sync_repository.dart`, `app_database.dart` (existing tables),
  `conflict_*_screen.dart`, `server/app/services/sync.py`, `server/app/routers/sync.py`.

### Phase 3 — AI / RAG (complete)
- Full pipeline in `brain/app/services/`: document loader (md/txt/pdf/json/csv/docx, **`/Secrets`
  hard-excluded**), 512-token chunker w/ 64 overlap, batch embedding via Ollama, ChromaDB store,
  incremental indexer (hash-diff: only changed files re-indexed, background task on startup),
  retriever + context assembler, streaming Ollama client.
- `POST /ask/ai/query` (proxied to brain), reindex, index-status endpoints.
- **AI file generation**: `/ask/generate-files` (+ dry-run) — the LLM can create files in the vault,
  with a sanitizer service and a filesystem-tree snapshot (`fs_tree`) for context.
- PDF text extraction endpoint used by the mobile explorer.
- Mobile chat UI with **server-synced chat sessions/history** (brain keeps a history DB; mobile has
  Drift tables `chat_sessions` / `chat_messages`) and a file-creation modal/worker.

### Phase 4 — Security (mostly complete)
- JWT (HS256) device auth. First device registers with a one-time **setup secret printed in jv-api
  logs on first boot**; more devices via `/auth/register/device`; reconnect + refresh flows.
- Device management: list devices, revoke device (Settings → Device Management screen).
- **End-to-end encrypted `/Secrets`**: AES-256-GCM + PBKDF2 (600k iterations), `.jvs` file format,
  cross-device secrets authorization (`/auth/authorize_secrets`) and revocation. Server never sees
  the passphrase; mobile crypto in `mobile/lib/features/secrets/domain/crypto_service.dart`.
- Secrets are excluded from sync-to-index and from RAG (tested: `test_rag_secrets_exclusion_e2e.py`).

### Extras already built
- Advanced file management in the app: multi-select, copy/cut/paste clipboard, search, breadcrumbs,
  context menus, PDF viewer/extract, markdown editor + viewer.
- Big test suites: `server/tests/` (auth, vault, sync, path validator, phase-4 e2e…) and
  `brain/tests/` (~28 files, incl. property-based tests with Hypothesis). Flutter tests in `mobile/test/`.

## 5. What is IN FLIGHT 🟡 (uncommitted work on `main` right now)

A **chat auto-archive** feature (staged but not committed as of 2026-07-08):
- `mobile/.../chat/data/chat_archive_service.dart` — sessions inactive > 7 days are written as
  markdown into `Memory/Chats/` in the vault (so the AI can later recall old conversations via RAG),
  then deleted locally. Runs at most once per 24 h.
- Drift schema bump + `chat_sessions` table changes, settings toggle, auth-provider changes,
  tests in `brain/tests/test_chat_archive.py`.
- ⚠️ `brain/test_archive.db` and `brain/test_history.db` are staged — these look like test artifacts
  and probably should be unstaged/gitignored before committing.

## 6. What is PENDING / NEXT 📋

1. **Finish & commit the chat-archive feature** (verify tests, drop the stray .db files).
2. **QR guest registration** — fully designed in `OFFICIAL-DOCS/11-QR-Guest-Registration.md`
  (invite tokens, guest-scoped JWTs, permission model), **zero code written yet**.
3. **Phase 5 — Polish & QA**: CI pipeline (GitHub Actions), migration test on a clean machine,
  backup/restore flow, performance checks, UI polish.
4. **Sync gaps**: no tombstones yet — **files deleted on the server can resurrect from a device**
  (known, documented non-goal for now). Background/auto sync, per-file version history, diff preview.
5. **Phase 6 ideas**: voice input (STT), smart tagging, biometric app lock, daily digest,
  relationship graph, web dashboard.

## 7. MUST-KNOW gotchas (hard-won, respect them)

1. **Ollama runs in Docker** (jv-ollama). Model weights live in the `jarvis-ollama-models` volume —
   after `docker exec jv-ollama ollama pull llama3` / `nomic-embed-text` once, they persist across
   restarts. Deleting the volume means re-downloading ~5 GB. On Windows with an NVIDIA GPU, start
   with the extra `docker-compose.gpu.yml` override for GPU inference; on Mac it runs CPU-only.
2. **ChromaDB image has no curl/wget** — you cannot healthcheck it with CMD curl; compose uses
   `service_started`, not `service_healthy`. Its API is **v2** (`/api/v2/heartbeat`); v1 is dead.
3. **Brain is port 8001, api and ChromaDB are both 8000 internally.** Don't mix them up.
4. Use **`chromadb-client`** (thin HTTP client) in brain, never the full `chromadb` package.
5. **`/Secrets` must NEVER be indexed or sent to the LLM.** The exclusion is a named constant in
   the document loader. There are e2e tests guarding this — keep them green.
6. **Never modify Phase 2 sync files** without reading `5-version-control.md` and the regression
   postmortems in `AI-AGENT-DOCS/AGENT-DEBUGGING/`. Sync bugs lose user data.
7. Mobile DB is **Drift with generated code** — after touching any `*_table.dart` or
   `app_database.dart`, run `dart run build_runner build` and write a schema migration
   (current schema version lives in `app_database.dart`; never edit existing tables' shape).
8. `.env` holds the JWT secret and vault path; it is **not** committed. `JARVIS_HOST_PATH` must
   point at an existing folder on the host or compose refuses to start.
9. The **setup secret** for registering the first device is printed in jv-api logs on first boot
   (`docker logs jv-api`) and is single-use.
10. All streaming is **NDJSON**; on Flutter, buffer until newline — a TCP chunk can contain a
   partial JSON line.

## 8. Tech stack cheat sheet

| Layer | Tech |
|---|---|
| Backend | Python 3.12/3.13, FastAPI, Pydantic v2, httpx, uvicorn |
| AI | Ollama (llama3:8b + nomic-embed-text), ChromaDB 0.5.23, tiktoken, PyMuPDF |
| Mobile | Flutter 3.38 / Dart ^3.10, Riverpod, Drift (SQLite), Dio, flutter_secure_storage, pointycastle |
| Infra | Docker Compose, Tailscale, JWT (HS256), AES-256-GCM + PBKDF2 |

## 9. Where to read more

- `01-Architecture.md` — topology, module boundaries, failure handling.
- `04-Sync-Protocol.md` + `AI-AGENT-DOCS/5-version-control.md` — sync, conflicts, guarantees.
- `05-AI-System-Design.md` — the RAG pipeline spec (primary reference for brain work).
- `06-Security-Model.md` / `07-Encryption-Design.md` — auth + secrets crypto.
- `10-Implementation-Phases.md` — the phase plan and exit criteria.
- `OFFICIAL-DOCS/walkthrough.md` — historical Phase 3 handover; good context, but the code has
  moved well past it (treat the code as truth).
