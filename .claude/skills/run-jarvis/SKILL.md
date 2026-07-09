---
name: run-jarvis
description: Run, start, smoke-test, or verify the JARVIS backend stack (docker compose: jv-api, jv-brain, jv-chromadb, jv-ollama) and build the Flutter mobile app. Use when asked to run the app, start the server, test the AI pipeline, or check that JARVIS works end-to-end.
---

# Run JARVIS

Backend = one docker compose stack (FastAPI api :8000, RAG brain, ChromaDB, Ollama).
Mobile = Flutter Android app in `mobile/`. All paths below are relative to the repo root.

## Run + verify (agent path)

```bash
bash .claude/skills/run-jarvis/smoke.sh
```

Starts the stack if down, then checks: api `/health`, brain↔ollama/chromadb reachability,
vault index status, and one end-to-end RAG query (`retrieval → llama3 → NDJSON stream`).
Ends with `SMOKE OK` and a JSON answer citing real vault files.

## One-time prerequisites

```bash
cp .env.template .env            # then set JARVIS_HOST_PATH (existing folder) + JARVIS_JWT_SECRET
docker compose up -d --build
docker exec jv-ollama ollama pull llama3            # ~4.6 GB
docker exec jv-ollama ollama pull nomic-embed-text  # ~274 MB
```

NVIDIA GPU (Windows/Linux): `docker compose -f docker-compose.yml -f docker-compose.gpu.yml up -d --build`

## Useful direct probes

```bash
curl http://localhost:8000/health                                   # api, no auth
docker exec jv-api curl -s http://brain:8001/brain/status           # brain deps
docker exec jv-api curl -s http://brain:8001/brain/index-status
docker exec jv-api curl -s -X POST http://brain:8001/brain/reindex  # force re-index
docker logs jv-api                                                  # setup secret on first boot
```

Public (`:8000`) endpoints other than `/health` need a JWT — the smoke script avoids this by
exec-ing curl inside `jv-api` on the internal Docker network, where brain has no auth.

## Mobile app

```bash
cd mobile
flutter pub get
dart run build_runner build --delete-conflicting-outputs   # required after Drift table changes
flutter analyze
flutter build apk --debug        # proves it compiles, no device needed
```

Human path: `flutter run` with an Android emulator/device attached; in-app server URL is
`http://10.0.2.2:8000` (emulator) or `http://<tailscale-ip>:8000` (phone).

## Gotchas (all hit for real)

- **Plain `docker compose up -d` recreates a GPU-started `jv-ollama` without GPU** (config diff
  triggers recreate). The smoke script therefore skips `up` when the stack is already running.
- **First AI query right after an Ollama (re)start can return `{"error": ""}`** — model still
  loading. Retry after ~30 s.
- **Index shows `error`/0 files if brain started before models were pulled** — startup indexing
  fails without `nomic-embed-text`. Fix: `POST /brain/reindex` (see probes above).
- **Port is 8000, not 8080.** ChromaDB also uses 8000 *internally*; brain is 8001. Don't mix.
- **`ollama pull` / `docker pull` dying with "TLS handshake timeout"** on this dev machine is the
  flaky host network, not the stack — restarting Docker Desktop (`wsl --shutdown` first) unwedges
  it; pulls resume partial progress on retry.
- The device **setup secret prints only on jv-api's first boot** (`docker logs jv-api`); it's
  single-use.
