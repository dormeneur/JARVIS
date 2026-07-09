# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

JARVIS: a self-hosted, offline-first personal knowledge OS. A plain-files vault folder (path set by
`JARVIS_HOST_PATH` in `.env`) is the single source of truth; everything else is derived. Three parts:
`server/` (jv-api, FastAPI), `brain/` (jv-brain, RAG pipeline), `mobile/` (Flutter Android app).
Full architecture, phase status, and gotchas: `PROJECT_OVERVIEW.md`. Setup: `HOW_TO_RUN.md`.

## Commands

```bash
# Backend stack (api :8000 host-exposed; brain :8001, chromadb, ollama internal-only)
docker compose up -d --build
docker compose -f docker-compose.yml -f docker-compose.gpu.yml up -d --build  # NVIDIA GPU

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

- **Request path:** mobile → jv-api (JWT auth) → jv-brain → jv-ollama/ChromaDB. Mobile never talks
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
- **`/Secrets` must never reach the AI.** Exclusion is a named constant in
  `brain/app/services/document_loader.py`; e2e guard: `brain/tests/test_rag_secrets_exclusion_e2e.py`.
  Secrets crypto (AES-256-GCM + PBKDF2, `.jvs` format) must stay byte-compatible between
  `mobile/lib/features/secrets/domain/crypto_service.dart` and the server.
- **Config:** all backend settings are env vars with `JARVIS_` prefix, loaded by pydantic-settings
  (`server/app/config.py`, `brain/app/config.py`) from the repo-root `.env` (never committed).
- **Auth:** first device registers with a one-time setup secret printed in jv-api logs on first
  boot; JWTs are HS256 signed with `JARVIS_JWT_SECRET`. Device registry lives in the vault at
  `system/devices.json`.
- **Port trap:** jv-api and ChromaDB both use 8000 internally; brain is 8001.
- ChromaDB: use the v2 API and the thin `chromadb-client` package (never full `chromadb`); the
  container image has no curl, so compose uses `service_started`, not healthchecks.
