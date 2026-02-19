# 08 — Deployment and Migration

## Scope

Defines Docker deployment procedures, machine migration strategy, backup/restore processes, and operational runbooks for the JARVIS system.

---

## Deployment Architecture

### Production Layout (Single Machine)
```
Host Machine (Windows 11 + WSL2)
├── Docker Desktop (WSL2 backend)
│   └── docker compose
│       ├── jv-api       (FastAPI)       → Port 127.0.0.1:8000
│       ├── jv-brain     (RAG engine)    → Internal only
│       ├── ollama       (LLM)           → Internal :11434
│       └── chromadb     (Vector store)  → Internal :8000
├── /JARVIS              (bind-mounted vault)
├── Tailscale            (mesh networking)
└── .env                 (configuration)
```

---

## Docker Compose Specification

```yaml
version: "3.9"

services:
  api:
    build:
      context: ./server
      dockerfile: Dockerfile
    container_name: jv-api
    ports:
      - "127.0.0.1:8000:8000"
    volumes:
      - ${JARVIS_HOST_PATH:?}:/data/JARVIS
    env_file: .env
    depends_on:
      ollama:
        condition: service_healthy
      chromadb:
        condition: service_healthy
    networks:
      - jarvis-net
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8000/health"]
      interval: 30s
      timeout: 10s
      retries: 3

  brain:
    build:
      context: ./brain
      dockerfile: Dockerfile
    container_name: jv-brain
    volumes:
      - ${JARVIS_HOST_PATH:?}:/data/JARVIS:ro
    env_file: .env
    depends_on:
      - ollama
      - chromadb
    networks:
      - jarvis-net
    restart: unless-stopped

  ollama:
    image: ollama/ollama:latest
    container_name: jv-ollama
    volumes:
      - ollama-models:/root/.ollama
    networks:
      - jarvis-net
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:11434/api/tags"]
      interval: 30s
      timeout: 10s
      retries: 3
    deploy:
      resources:
        limits:
          memory: 8G

  chromadb:
    image: chromadb/chroma:latest
    container_name: jv-chromadb
    volumes:
      - chroma-data:/chroma/chroma
    networks:
      - jarvis-net
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8000/api/v1/heartbeat"]
      interval: 30s
      timeout: 10s
      retries: 3

volumes:
  ollama-models:
    name: jarvis-ollama-models
  chroma-data:
    name: jarvis-chroma-data

networks:
  jarvis-net:
    name: jarvis-internal
    driver: bridge
```

---

## Environment Configuration (`.env`)

```env
# === Vault ===
JARVIS_HOST_PATH=B:/JARVIS

# === API ===
JARVIS_JWT_SECRET=<generated-256-bit-hex>
JARVIS_JWT_EXPIRY_HOURS=720
JARVIS_LOG_LEVEL=INFO
JARVIS_MAX_UPLOAD_MB=100

# === AI ===
JARVIS_OLLAMA_URL=http://ollama:11434
JARVIS_VECTORDB_URL=http://chromadb:8000
JARVIS_EMBEDDING_MODEL=nomic-embed-text
JARVIS_LLM_MODEL=llama3

# === Security ===
JARVIS_CORS_ORIGINS=
JARVIS_RATE_LIMIT_PER_MINUTE=60
```

> [!WARNING]
> `.env` must **never** be committed to version control. Add to `.gitignore` immediately on project init. Provide `.env.template` with placeholder values.

---

## First-Time Setup

### Prerequisites
1. Docker Desktop installed and running (WSL2 backend)
2. Tailscale installed and logged in
3. `/JARVIS` folder exists (or will be created)

### Setup Commands
```powershell
# 1. Clone repo
git clone <repo-url> jarvis-system
cd jarvis-system

# 2. Create .env from template
Copy-Item .env.template .env
# Edit .env — set JARVIS_HOST_PATH and generate JWT_SECRET

# 3. Generate JWT secret
python -c "import secrets; print(secrets.token_hex(32))"
# Paste result into .env JARVIS_JWT_SECRET

# 4. Create vault directory (if not exists)
New-Item -ItemType Directory -Path "B:\JARVIS" -Force

# 5. Start all services
docker compose up -d

# 6. Pull Ollama model (first time only)
docker exec jv-ollama ollama pull llama3
docker exec jv-ollama ollama pull nomic-embed-text

# 7. Verify health
curl http://localhost:8000/health
# Expected: {"status": "ok"}

# 8. Initial device registration
# Visit setup UI or use:
curl -X POST http://localhost:8000/auth/register \
  -H "Content-Type: application/json" \
  -d '{"device_name": "laptop", "setup_secret": "<from server log>"}'
```

---

## Migration Procedure

### Why Migration is Simple
- All user data lives in `/JARVIS` (plain files)
- Docker containers are stateless (config via `.env`)
- Vector index is re-buildable from vault files
- Ollama models are re-downloadable

### Migration Steps (Machine A → Machine B)

```
Machine A (source):                    Machine B (target):

1. docker compose down                4. Install Docker Desktop
                                      5. Install Tailscale, login
2. Copy /JARVIS folder ──────────────► 6. Paste /JARVIS folder
3. Copy project folder ──────────────► 7. Paste project folder
   (server/, brain/, docker-compose,      
    .env)                              8. Edit .env (update paths if needed)
                                      9. docker compose up -d
                                      10. docker exec jv-ollama ollama pull llama3
                                      11. Verify: curl localhost:8000/health
```

### What Needs Updating After Migration
| Item | Action Required |
|---|---|
| `JARVIS_HOST_PATH` in `.env` | Update to new machine's path |
| Tailscale device | New machine auto-joins mesh; update ACLs if needed |
| Ollama models | Re-pull (not in `/JARVIS`) |
| Vector index | Auto re-indexes on first startup |
| JWT tokens | Existing mobile tokens remain valid (same JWT secret) |
| Device registrations | Preserved in vault config |

### Migration Validation Checklist
- [ ] `docker compose up -d` succeeds
- [ ] `GET /health` returns `200`
- [ ] Mobile app connects over Tailscale
- [ ] File listing matches pre-migration
- [ ] AI query returns results
- [ ] Secrets decrypt with correct passphrase

---

## Backup Strategy

### Automated Vault Backup
```powershell
# Scheduled task (weekly recommended)
$timestamp = Get-Date -Format "yyyy-MM-dd"
$backupPath = "D:\Backups\JARVIS-$timestamp.zip"
Compress-Archive -Path "B:\JARVIS" -DestinationPath $backupPath -Force
```

### Backup Components
| Component | Backup Method | Priority |
|---|---|---|
| `/JARVIS` folder | File copy or zip archive | **Critical** |
| `.env` file | Secure copy (contains JWT secret) | **Critical** |
| Docker Compose files | Included in project repo | Medium |
| Ollama models | Re-downloadable; skip | Low |
| Vector DB | Re-indexable from vault; skip | Low |

### Backup API Endpoint
```
POST /backup/export
Response: 202 Accepted
  {"backup_id": "uuid", "status": "generating"}

GET /backup/download/{backup_id}
Response: 200 OK (application/zip)
  → Streaming zip of /JARVIS contents
```

### Restore from Backup
```powershell
# 1. Stop services
docker compose down

# 2. Extract backup
Expand-Archive -Path "D:\Backups\JARVIS-2026-02-19.zip" -DestinationPath "B:\"

# 3. Restart services
docker compose up -d

# 4. Trigger re-index
curl -X POST http://localhost:8000/ask/reindex
```

---

## Operational Runbooks

### Runbook: Service Won't Start
```
1. Check Docker Desktop is running
2. docker compose logs api → look for errors
3. Verify .env exists and JARVIS_HOST_PATH is valid
4. Verify /JARVIS directory exists and is accessible
5. Check port 8000 isn't already in use: netstat -an | findstr 8000
6. Try: docker compose down && docker compose up -d --build
```

### Runbook: Mobile App Can't Connect
```
1. Verify Tailscale is running on both devices
2. Check: tailscale status → both devices listed?
3. Ping server from mobile: ping 100.x.y.z
4. Verify API is up: curl http://100.x.y.z:8000/health
5. Check JWT token hasn't expired
6. Verify server isn't bound to 127.0.0.1 only (Tailscale needs host binding)
```

> [!IMPORTANT]
> Tailscale typically routes to host ports. If `jv-api` binds to `127.0.0.1:8000`, Tailscale traffic arriving at `100.x.y.z:8000` may not reach it. The solution is to bind to `0.0.0.0:8000` **only** if the host firewall blocks non-Tailscale interfaces, **or** use Tailscale's `--accept-routes` to route within the container. This needs validation during implementation.

### Runbook: AI Queries Failing
```
1. docker exec jv-ollama ollama list → model present?
2. docker logs jv-ollama → OOM or error?
3. docker logs jv-brain → connection refused to Ollama?
4. curl http://localhost:11434/api/tags → Ollama healthy?
5. If model missing: docker exec jv-ollama ollama pull llama3
6. If OOM: increase Docker memory limit in docker-compose.yml
```

### Runbook: Disk Space Low
```
1. docker system df → check Docker usage
2. docker system prune → remove unused images/containers
3. Check /JARVIS size: Get-ChildItem -Recurse B:\JARVIS | Measure-Object -Property Length -Sum
4. Check Ollama models: docker exec jv-ollama du -sh /root/.ollama
5. Consider removing unused Ollama models
```

---

## Monitoring

### Health Endpoints
| Endpoint | Checks |
|---|---|
| `GET /health` | API is running |
| `GET /status` | Vault stats, disk usage, service connectivity |
| `GET /ask/status` | Ollama reachable, VectorDB reachable, model loaded |

### Recommended Monitoring
- Check `GET /health` every 60 seconds from Tailscale-connected device
- Alert if unhealthy for >5 minutes
- Log rotation for audit logs (max 50MB, 5 files)

---

## Future Extensibility

- **Auto-update**: Watch for new Docker images; pull and restart
- **Remote management dashboard**: Web UI for health monitoring
- **Multi-vault support**: Different JARVIS folders for different personas
- **Scheduled backups**: Cron-based automatic backup to external drive
