# Development plan — chunked for an AI coding agent (concise)

Below are 2 levels of chunking + small 3rd-level sub-tasks for each smaller chunk. Follow this order (MVP first). Keep tasks small, run tests after each subtask, and allow the agent to ask 1–2 clarifying questions before starting a chunk.

---

## Level 1 — Big chunks (high level)

1. Project scaffold & portability
2. Core backend: file vault + API + sync
3. Mobile app: UI + local cache + sync client
4. AI layer: indexing + Ollama integration + RAG
5. Security & networking (Tailscale, auth, encryption)
6. QA, CI, docs & migration

---

## Level 2 — Smaller chunks with sub-tasks

### 1. Project scaffold & portability

* 1.1 Repo + mono repo layout

  * create repo structure (`/server`, `/mobile`, `/infra`, `/docs`)
  * add README, LICENSE, contribution guide
* 1.2 Docker + docker-compose MVP

  * create `docker-compose.yml` with `api`, `ollama`, `db` (volume mount `/JARVIS`)
  * verify `docker compose up` starts services
* 1.3 Config & secrets handling

  * add `.env` template and config loader in server
  * ensure `client_secret` & API tokens not committed

### 2. Core backend: file vault + API + sync

* 2.1 Basic file API (MVP)

  * implement list/create/read/delete endpoints for files (`/files`)
  * unit test filesystem ops (using temp directories)
* 2.2 Metadata & sync endpoints

  * add file metadata (path, last_modified, hash, sync_enabled)
  * implement `/sync/pull` and `/sync/push` minimal payloads
* 2.3 Upload/download + streaming

  * implement chunked upload and secure download endpoint
  * add file conflict handling (SQLite MutationQueue with local snapshot)
* 2.4 Backup & export

  * endpoint to export JARVIS archive (zip) for migration

### 3. Mobile app: UI + local cache + sync client

* 3.1 File explorer + markdown editor (MVP)

  * folder tree UI, open file, edit markdown, save locally
  * local DB (SQLite) for metadata
* 3.2 Sync client basic

  * implement push/pull logic using timestamps and hashes
  * per-folder sync toggle in UI
* 3.3 Upload media + download preview

  * upload images/docs, preview files, caching
* 3.4 Offline mode behaviors

  * detect server health, disable AI actions, queue outgoing changes

### 4. AI layer: indexing + Ollama integration + RAG

* 4.1 Document loaders & text extraction

  * implement md loader, PDF text extract, images -> captions (optional)
* 4.2 Embeddings & vector store (local)

  * create embedding pipeline, store vectors in local vector DB (Chroma/pgvector)
* 4.3 /ask endpoint & retrieval

  * implement `POST /ask` that retrieves context, calls Ollama, returns answer
* 4.4 Attachments & size limits

  * implement attachment referencing, chunking long docs, streaming responses

### 5. Security & networking

* 5.1 Tailscale integration (recommended)

  * document onboarding steps, test device pairing
* 5.2 API auth & session tokens

  * implement JWT or token-based auth; token per device
* 5.3 Secrets encryption

  * encrypt `/Secrets` files with AES; key stored only on devices
* 5.4 Harden endpoints

  * rate limits, input validation, CORS only for allowed origins

### 6. QA, CI, docs & migration

* 6.1 Tests & smoke checks

  * unit tests for file ops, integration tests for sync, e2e for mobile-server roundtrip
* 6.2 CI pipeline

  * GitHub Actions: lint, test, build docker image
* 6.3 Docs & runbook

  * install/migration README, backup/restore steps, Tailscale setup
* 6.4 Migration and restore test

  * test copying `/JARVIS` + docker compose restart on fresh machine

---

# Short summary paragraph

Break the project into six big areas (scaffold, backend, mobile, AI, security, QA). For each area implement a minimal, testable MVP: Dockerized server + file API mounted to `/JARVIS`, a Flutter file explorer with local SQLite and selective sync, a simple retrieval pipeline that feeds selected files into Ollama via `POST /ask`, and a private network access model using Tailscale plus token/JWT auth and AES-encrypted secrets. Iterate: implement small endpoints and UI features, add sync and conflict resolution, then integrate embeddings and vector search, and always run unit/integration tests and a migration/export flow so the whole system remains portable and auditable.

If you want, I’ll now convert the first small task (Repo + docker-compose with `/JARVIS` mount and a Health endpoint) into a concrete agent prompt you can hand off.
