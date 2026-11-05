# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JARVIS: a self-hosted, offline-first personal knowledge OS. A plain-files vault folder (path set by
`JARVIS_HOST_PATH` in `.env`) is the single source of truth; everything else is derived. Four parts:
`server/` (jv-api, FastAPI), `brain/` (jv-brain, RAG pipeline), `mobile/` (Flutter Android app) and
`desktop/` (the Windows cursor companion, .NET 9 / WinUI 3; its own guide is `desktop/CLAUDE.md`).
Full architecture, phase status, and gotchas: `PROJECT_OVERVIEW.md`. Setup: `HOW_TO_RUN.md`.

## The privacy split (applies to every part)

- **Vault content never reaches a cloud model.** Anything answered from the vault (notes,
  `Profile/`, memory, form answers) is answered by the local brain: jv-brain + Ollama.
- The desktop app may use the owner's `claude` CLI for general screen help (pointing, drawing,
  "how do I…"), but those turns carry no vault data. A request that needs the vault is routed to
  the local brain instead; Private mode routes everything, screenshots included, locally.
- The desktop app reaches the vault only through jv-api on `127.0.0.1:8000`, as a registered
  device, so the `/Secrets` exclusion and auth apply to it like to any phone.

## Commands

```bash
# Desktop (from desktop/; .NET SDK is per-user, see desktop/CLAUDE.md)
dotnet build -p:Platform=x64
dotnet test tests/Jarvis.Tests

# Backend stack (api :8000 host-exposed; brain :8001 and chromadb internal-only).
# Ollama runs on the host (ollama.com install), reached as host.docker.internal:11434.
docker compose up -d --build

# Python tests (run from server/ or brain/ — tests import `app.*`)
cd server && python -m pytest tests/                       # all
cd brain  && python -m pytest tests/test_chunking_logic.py::test_name  # single

# Mobile
cd mobile
flutter pub get
dart run build_runner build --delete-conflicting-outputs   # REQUIRED after any Drift table change
flutter test
flutter analyze
flutter run
```

## Architecture facts that span files

- **Request path:** mobile → jv-api (JWT auth) → jv-brain → host Ollama/ChromaDB. Mobile never talks
  to brain directly; every brain route gets a `/ask/*` proxy in `server/app/routers/ask.py`.
- **Streaming is NDJSON at every hop** (ollama → brain → api → mobile). On the Flutter side a TCP
  chunk may hold a partial JSON line — buffer to newline.
- **Sync engine is version-based optimistic concurrency** (`baseVersion` vs `serverVersion`,
  per-file). The spec is `DOCUMENTATION/AI-AGENT-DOCS/5-version-control.md`; the implementation is
  `mobile/lib/features/sync/data/sync_repository.dart` + `server/app/services/sync.py`. These files
  are considered LOCKED — regressions here lose user data; read the spec and the postmortems in
  `DOCUMENTATION/AI-AGENT-DOCS/AGENT-DEBUGGING/` before touching them. Every conflict must map to a
  `MutationQueue` row; there are no conflict files on disk.
- **Mobile DB is Drift with codegen.** Schema version and migrations live in
  `mobile/lib/core/storage/app_database.dart`. Never alter existing tables' shape; add migrations.
- **Form filling (desktop):** `/ask/ai/form-answers` (Profile/ + attached resume/context files + nearest
  vault passages; an answer survives only if its quoted evidence is in the cited file) and
  `/ask/ai/compose-answer` (drafts "why us?" answers) in `brain/app/routers/forms.py`. Answers the
  user used live in `Profile/form-memory.json`; each form keeps `Profile/forms/<title>/answers.md`.
  Local-only invite codes for a device on the host: `docker exec jv-api python -m app.invite`.
- **Agentic chat:** `/ask/ai/agent` → `brain/app/routers/agent.py` runs an Ollama `/api/chat`
  tool-calling loop. Tools live in `brain/app/services/tools.py` (schema + risk + executor in
  one place). Mutating tools stop the stream with `approval_required`; the client re-POSTs with
  `tool_transcript` to resume, so approvals need no server state. **Requires a tool-capable model**
  (`JARVIS_LLM_MODEL=qwen3:4b`) — llama3 has no tool template and will narrate shell commands
  instead of calling tools. Safety tests: `brain/tests/test_tools_safety.py`.
- **Brain-private state** lives in `<vault>/.system/brain/` (chat history, `traces.jsonl`, `fts.db`
  keyword index, `proposals.db`, `scheduler.json`, `trash/`): hidden from sync and indexing, included in
  backups. SQLite there uses `journal_mode=DELETE`, never WAL (unreliable on Docker Desktop bind mounts).
- **Agent resume is server-authored:** each executed tool call carries its `result` in the transcript the
  server returns with `approval_required`; the client sends it back and the server never re-runs an entry
  that has a result. `delete_file` always asks (no session grant). Files are changed with `append_to_file` /
  `replace_in_file`; `edit_file` refuses a rewrite that looks truncated, and every overwrite or delete keeps
  the old version in `.system/brain/trash/` for 30 days. Retrieved vault text is wrapped in `<vault_file>`
  tags (`services/vault_text.py`) so it is treated as data, not instructions.
- **Search is hybrid:** Chroma vectors + SQLite FTS5 (BM25) fused with RRF (`services/retriever.py`,
  `keyword_index.py`). Re-embedding is driven by `INDEX_VERSION` in `incremental_indexer.py`: bump it when
  what gets embedded changes. Measure changes with `python -m app.evals.retrieval` (golden set stays in the
  vault, never in git) and `python -m app.evals.traces`.
- **Background AI work goes through the Inbox:** the memory extractor (user's words only) and later
  features file *proposals* (`services/proposals.py`, `/ask/proposals`); nothing is written until the user
  approves on the phone. The scheduler (`services/scheduler.py`) runs reindex, the daily digest, trash
  pruning and proposal expiry. `python scripts/doctor.py` checks the whole stack.
- **Learning from the user's documents** is `python -m app.services.profile_import` (local model only): facts about
  the user found in `Personal/` (or `--folder X`) become Inbox proposals to append to `Profile/<topic>.md`; ID and
  secret-like facts are dropped; files are remembered by hash. No model is ever fine-tuned: retrieval plus the
  approved Profile files are the memory.
- **`/Secrets` must never reach the AI.** Exclusion is a named constant in
  `brain/app/services/document_loader.py`; e2e guard: `brain/tests/test_rag_secrets_exclusion_e2e.py`.
  The tool layer enforces the same list in `tools.safe_path()` — every model-supplied path is
  resolved and re-checked against the vault root before any read or write.
  Secrets crypto (AES-256-GCM + PBKDF2, `.jvs` format) must stay byte-compatible between
  `mobile/lib/features/secrets/domain/crypto_service.dart` and the server.
- **Config:** all backend settings are env vars with `JARVIS_` prefix, loaded by pydantic-settings
  (`server/app/config.py`, `brain/app/config.py`) from the repo-root `.env` (never committed).
- **Auth:** first device registers with a one-time setup secret printed in jv-api logs on first
  boot; JWTs are HS256 signed with `JARVIS_JWT_SECRET`. Device registry lives in the vault at
  `system/devices.json`.
- **Port trap:** jv-api and ChromaDB both use 8000 internally; brain is 8001; jv-jobs is 8002.
- **Job search (`jobs/`, jv-jobs, compose profile `jobs`):** runs the external ai-job-search
  workspace through headless Claude Code on the owner's subscription. Never edit that workspace's
  files from JARVIS; adapt via CLI flags and the headless contract in `jobs/app/runner.py`. It never
  mounts the vault. Admin-only proxy at `/jobs/*`. Spec: `OFFICIAL-DOCS/12-Job-Search-Integration.md`.
  Tests: `cd jobs && python -m pytest` (a fake `claude` replays stream-json; no tokens spent).
- **Vectors live in SQLite by default** (`JARVIS_VECTOR_BACKEND=sqlite`, `services/sqlite_vector_store.py`,
  `<vault>/.system/brain/vectors.db`, brute-force cosine). ChromaDB is kept only as a rollback
  (`=chroma`): use its v2 API and the thin `chromadb-client` package (never full `chromadb`); the container
  image has no curl, so compose uses `service_started`, not healthchecks.
