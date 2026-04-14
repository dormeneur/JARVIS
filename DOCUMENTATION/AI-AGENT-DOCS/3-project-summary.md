# JARVIS — Project Summary (concise)

**Purpose:** Private, portable, encrypted AI personal knowledge OS (file-first vault + AI assistant).

## Core Modules

* **Vault (jv-vault)** — single `/JARVIS` folder (markdown + media). Source of truth.
* **API (jv-api)** — FastAPI Docker service exposing secure file operations (list/create/read/update/delete).
* **Sync Engine (jv-sync)** — selective two-way sync, timestamp/hash conflict resolution, SQLite-managed conflicts.
* **Mobile Client (jv-app)** — Flutter file-manager UI, offline mirror, local metadata (SQLite), selective sync toggles.
* **AI Layer (jv-brain)** — local LLM (Ollama) + vector index (pgvector or Chroma) for RAG and contextual QA.
* **Secrets Service (jv-secrets)** — AES-256 encrypted folder with PBKDF2 key derivation; mobile decrypt locally.
* **Auth & Networking (jv-sec)** — Tailscale private network + JWT/API tokens; no public ports.

## Data Layout (recommended)

```
/JARVIS
  /Personal
  /Education
  /Work
  /Beliefs
  /Constraints
  /Commitments
  /Secrets (encrypted)
  /Goals
  /TechProfile
  /People
  /Likes
  /Ideas
```

## Key Features (short)

* File-first, portable (Docker compose).
* Offline-first mobile app with selective mirror.
* Local RAG-powered assistant using your files as context.
* Strong device-limited security (Tailscale + encrypted secrets).
* Simple migration: copy `/JARVIS` + Docker files → `docker compose up`.

## Security Rules (must-follow)

1. No public exposed ports.
2. Tailscale-only connectivity for devices.
3. JWT/API auth for app requests.
4. Encrypt `/Secrets` at-rest with AES.
5. Keep backup copies offline/encrypted.

## Implementation Phases (priority)

1. Vault + FastAPI file endpoints + basic Flutter viewer + Tailscale.
2. Sync engine, offline cache, conflict handling.
3. Ollama integration, RAG indexing, attachments.
4. Secrets encryption, voice STT/TTS, smart tagging/relationship graph.

## Short next steps

1. Initialize repo + `docker-compose.yml`.
2. Implement minimal FastAPI endpoints for file ops.
3. Build Flutter folder tree viewer + markdown editor.
4. Wire Tailscale auth; test local-only access.

