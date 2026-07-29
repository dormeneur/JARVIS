#!/usr/bin/env bash
# Smoke-drive the JARVIS backend stack end-to-end.
# Usage (from repo root):  bash .claude/skills/run-jarvis/smoke.sh
# Needs: Docker running, .env present (cp .env.template .env), models pulled once
#        (docker exec jv-ollama ollama pull llama3 && docker exec jv-ollama ollama pull nomic-embed-text)
set -e

echo "== start containers =="
# Only 'up' when the stack isn't running — a plain 'up' would recreate
# containers started with the docker-compose.gpu.yml override and drop GPU.
if ! docker ps --format '{{.Names}}' | grep -q '^jv-api$'; then
  docker compose up -d
fi
docker ps --filter name=jv- --format '{{.Names}}: {{.Status}}'

echo "== api health (host-exposed :8000) =="
curl -sf http://localhost:8000/health

echo
echo "== brain status (internal network, via jv-api) =="
docker exec jv-api curl -sf http://brain:8001/brain/status

echo
echo "== vault index status =="
docker exec jv-api curl -sf http://brain:8001/brain/index-status

echo
echo "== end-to-end AI query (retrieval -> llama3 -> NDJSON stream) =="
# Hits brain directly over the internal network, so no JWT needed.
docker exec jv-api curl -s -m 180 -X POST http://brain:8001/brain/ai/query \
  -H 'Content-Type: application/json' \
  -d '{"query": "Reply with exactly: JARVIS ONLINE"}' | tail -1

echo
echo "SMOKE OK"
