# JARVIS Phase 3 — AI Integration Handover Document

**Author:** AI Agent (Antigravity)  
**Date:** 2026-03-24  
**Status:** Step 1 of 12 complete. Steps 2–12 pending.

---

## 1. What is JARVIS?

JARVIS is a **private, offline-first AI personal knowledge OS**. All user data lives as plain markdown files in a `/JARVIS` vault folder. The system has three layers:

| Layer | Name | Tech | Status |
|---|---|---|---|
| Backend API | `jv-api` | Dockerized FastAPI (Python 3.13) | ✅ Running |
| AI/RAG Engine | `jv-brain` | Dockerized FastAPI (Python 3.12) | 🟡 Scaffold only |
| Mobile App | `jv-app` | Flutter 3.38 + Drift SQLite + Riverpod | ✅ Running |
| LLM Inference | Ollama | Native Windows install (v0.16.2) | ✅ Running |
| Vector Store | ChromaDB | Docker container | ✅ Running |

**Networking:** All communication is over Tailscale (WireGuard VPN). No public ports. Docker containers use `jarvis-internal` bridge network.

---

## 2. What Has Been Completed

### Phase 2: Sync & Conflict Resolution ✅ (LOCKED — DO NOT MODIFY)

The entire sync engine and conflict resolution system is complete and stable. Here is what was built:

- **4-phase sync engine** in [sync_repository.dart](file:///b:/DEV/JARVIS/mobile/lib/features/sync/data/sync_repository.dart): manifest diff → push → pull → resolve
- **Conflict detection:** When both mobile and server change the same file, a single [MutationQueue](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart#30-46) row is created with `status='failed'` and the local content stored in `localContentSnapshot`
- **No garbage files:** Server no longer creates [_conflict_](file:///b:/DEV/JARVIS/server/tests/test_sync.py#937-958) files on disk. Conflict state is purely in SQLite
- **2-step resolution UI** in [conflict_detail_screen.dart](file:///b:/DEV/JARVIS/mobile/lib/features/sync/presentation/conflict_detail_screen.dart): Compare (read-only Local vs Remote tabs) → Edit (pick a base, modify, save)
- **Unified [resolveConflict(mutationId, finalContent)](file:///b:/DEV/JARVIS/mobile/lib/features/sync/data/sync_repository.dart#663-724)** method replaces the old three separate methods
- **Schema v5:** Added `localContentSnapshot` TEXT column to [MutationQueue](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart#30-46) table

> [!CAUTION]
> **DO NOT modify any of these files.** They are locked and battle-tested:
> - [sync_repository.dart](file:///b:/DEV/JARVIS/mobile/lib/features/sync/data/sync_repository.dart), [app_database.dart](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart), [conflict_detail_screen.dart](file:///b:/DEV/JARVIS/mobile/lib/features/sync/presentation/conflict_detail_screen.dart)
> - [conflict_list_screen.dart](file:///b:/DEV/JARVIS/mobile/lib/features/sync/presentation/conflict_list_screen.dart), [conflict_provider.dart](file:///b:/DEV/JARVIS/mobile/lib/features/sync/presentation/conflict_provider.dart)
> - [server/app/services/sync.py](file:///b:/DEV/JARVIS/server/app/services/sync.py), [server/app/routers/sync.py](file:///b:/DEV/JARVIS/server/app/routers/sync.py)

### Phase 3 Step 1: Docker Infrastructure ✅

Added ChromaDB and `jv-brain` scaffold to the Docker stack. Key decisions and gotchas documented below.

---

## 3. Current System State (as of 2026-03-24)

### 3.1 Container Topology

```
Host Machine (Windows 11, i7-12650H, 16GB RAM, RTX 3050 6GB)
├── Ollama v0.16.2 (NATIVE, not in Docker)
│   ├── llama3:8b (4.6 GB, GPU-accelerated)
│   └── nomic-embed-text (NOT YET INSTALLED — pull before Step 5)
│   └── Listening on 0.0.0.0:11434
│
├── Docker Desktop (WSL2 backend)
│   └── docker compose (jarvis-internal network)
│       ├── jv-api       (FastAPI)     → 0.0.0.0:8000:8000 (host-exposed)
│       ├── jv-brain     (FastAPI)     → port 8001 (internal only)
│       └── jv-chromadb  (ChromaDB)    → port 8000 (internal only)
│
├── B:\JARVIS (vault folder, bind-mounted into containers)
└── Tailscale (mesh VPN for mobile access)
```

### 3.2 File Tree — What Exists Now

```
b:\DEV\JARVIS\
├── docker-compose.yml          ← 3 services: api, brain, chromadb
├── .env                        ← All config (vault path, JWT, AI URLs)
│
├── server/                     ← jv-api (Phase 1+2, LOCKED)
│   ├── Dockerfile
│   ├── requirements.txt
│   └── app/
│       ├── main.py             ← FastAPI app (health, auth, sync, files routers)
│       ├── config.py           ← Pydantic settings (JARVIS_ prefix)
│       ├── routers/
│       │   ├── health.py       ← GET /health
│       │   ├── auth.py         ← POST /auth/register, /auth/login
│       │   ├── files.py        ← CRUD on /files/*
│       │   └── sync.py         ← POST /sync/manifest-diff, /sync/push, /sync/pull
│       ├── services/
│       │   ├── vault.py        ← Filesystem abstraction
│       │   ├── sync.py         ← Sync engine (version tracking, conflict detection)
│       │   ├── auth.py         ← JWT + device registration
│       │   ├── path_validator.py
│       │   └── version_tracker.py
│       └── models/
│           └── sync_models.py
│
├── brain/                      ← jv-brain (SCAFFOLD ONLY — Steps 2–10 build this out)
│   ├── Dockerfile              ← Python 3.12-slim, port 8001, has curl for healthcheck
│   ├── requirements.txt        ← fastapi, uvicorn, pydantic-settings, httpx (minimal)
│   └── app/
│       ├── __init__.py
│       ├── config.py           ← BrainSettings: vault_path, ollama_url, vectordb_url, models
│       └── api.py              ← GET /brain/status (checks Ollama + ChromaDB reachability)
│
└── mobile/                     ← jv-app (Phase 2, LOCKED except for NEW features/ai_chat/)
    └── lib/
        ├── core/
        │   ├── storage/app_database.dart  ← Drift schema v5 (DO NOT modify existing tables)
        │   └── network/api_client.dart
        └── features/
            ├── explorer/       ← File browser
            ├── editor/         ← Markdown editor
            ├── sync/           ← Sync engine + conflict resolution (LOCKED)
            ├── settings/       ← App settings
            └── ai_chat/        ← DOES NOT EXIST YET (Step 12 creates this)
```

### 3.3 Environment Variables ([.env](file:///b:/DEV/JARVIS/.env))

```env
# Vault
JARVIS_HOST_PATH=B:/JARVIS          # Host path, bind-mounted into containers
JARVIS_VAULT_PATH=/data/JARVIS       # Path INSIDE containers

# Auth
JARVIS_JWT_SECRET=<redacted>         # HS256 signing key
JARVIS_JWT_EXPIRY_HOURS=720          # 30 days

# AI (added in Step 1)
JARVIS_OLLAMA_URL=http://host.docker.internal:11434   # Native Ollama on host
JARVIS_VECTORDB_URL=http://chromadb:8000              # ChromaDB container
JARVIS_BRAIN_URL=http://brain:8001                    # jv-brain container
JARVIS_EMBEDDING_MODEL=nomic-embed-text
JARVIS_LLM_MODEL=llama3
```

---

## 4. Critical Gotchas Discovered During Step 1

These are hard-won lessons. Anyone continuing this work **must** know these:

### 🔴 Gotcha 1: Ollama Must Bind to `0.0.0.0`

Ollama defaults to binding on `127.0.0.1:11434`. Docker containers **cannot reach** `127.0.0.1` on the host. You must start Ollama with:

```powershell
$env:OLLAMA_HOST="0.0.0.0"
ollama serve
```

Or set `OLLAMA_HOST=0.0.0.0` as a permanent system environment variable. Without this, `jv-brain` will report `"ollama": "unreachable"`.

### 🔴 Gotcha 2: ChromaDB Image Has No `curl`

The `chromadb/chroma:latest` Docker image is a minimal Rust binary. It contains **no** `curl`, `wget`, `python`, or any standard HTTP tools. You **cannot** use `CMD curl` in a healthcheck. We removed the healthcheck entirely and use `condition: service_started` instead of `service_healthy` in `depends_on`.

### 🔴 Gotcha 3: ChromaDB API is v2, Not v1

The latest ChromaDB image uses `/api/v2/heartbeat` (returns `{"nanosecond heartbeat": ...}`). The `/api/v1/heartbeat` endpoint returns an error: `"The v1 API is deprecated"`. All ChromaDB client code must use v2 APIs.

### 🟡 Gotcha 4: `nomic-embed-text` Not Yet Installed

The model pull was interrupted. Before Step 5 (Embedding Pipeline), run:
```powershell
ollama pull nomic-embed-text
```
This is ~274 MB. Verify with `ollama list`.

### 🟡 Gotcha 5: Brain Uses Port 8001, Not 8000

ChromaDB and `jv-api` both use port 8000 internally. To avoid confusion during debugging, `jv-brain` listens on **port 8001**. All references (Dockerfile `EXPOSE`, healthcheck URL, `JARVIS_BRAIN_URL`) must use 8001.

---

## 5. Reference Documents

All design specs live in `b:\DEV\JARVIS\DOCUMENTATION\OFFICIAL-DOCS\`. You **must** read these before implementing:

| Document | What It Covers | Critical For |
|---|---|---|
| [05-AI-System-Design.md](file:///b:/DEV/JARVIS/DOCUMENTATION/OFFICIAL-DOCS/05-AI-System-Design.md) | **PRIMARY.** Full RAG pipeline spec: loader, chunker, embedder, retriever, context assembler, Ollama client, POST /ask contract, index status endpoint | Steps 2–10 |
| [02-Backend-Specification.md](file:///b:/DEV/JARVIS/DOCUMENTATION/OFFICIAL-DOCS/02-Backend-Specification.md) | Existing API routes, error codes, ask endpoint proxy contract | Step 11 |
| [03-Mobile-Architecture.md](file:///b:/DEV/JARVIS/DOCUMENTATION/OFFICIAL-DOCS/03-Mobile-Architecture.md) | Flutter feature structure, `chat_history` SQLite schema, AI chat feature spec | Step 12 |
| [08-Deployment-and-Migration.md](file:///b:/DEV/JARVIS/DOCUMENTATION/OFFICIAL-DOCS/08-Deployment-and-Migration.md) | Docker Compose patterns, .env layout, healthcheck conventions | Steps 1–2 |
| [10-Implementation-Phases.md](file:///b:/DEV/JARVIS/DOCUMENTATION/OFFICIAL-DOCS/10-Implementation-Phases.md) | Phase 3 deliverables table (items 3.1–3.11) — the checklist | All steps |

---

## 6. Remaining Steps — Detailed Implementation Guide

### Step 2: Brain Service Scaffold

**Goal:** The `jv-brain` service is currently a single healthcheck endpoint. This step adds proper project structure and Pydantic request/response models.

**Files to create:**
- `brain/app/models.py` — Pydantic models: `AskRequest`, `AskResponse`, `IndexStatus`, `Source`

**What `AskRequest` must contain** (from 05-AI §POST /ask):
```python
class AskRequest(BaseModel):
    query: str
    attachments: list[str] = []         # vault-relative file paths
    options: AskOptions | None = None

class AskOptions(BaseModel):
    top_k: int = 5
    filter_paths: list[str] = []
    include_sources: bool = True
    stream: bool = True
```

**What `AskResponse` must contain:**
```python
class Source(BaseModel):
    path: str
    chunk: int
    score: float

class AskResponse(BaseModel):
    answer: str
    sources: list[Source]
    model: str
    tokens_used: int
```

**Test:** Import models in a Python shell, validate serialization. No HTTP test needed yet.

**Dependencies:** None beyond Step 1.

---

### Step 3: Document Loader

**Goal:** Read all supported files from the vault and extract their text content.

**File to create:** `brain/app/document_loader.py`

**Spec reference:** 05-AI §Document Loader

**Supported types:**

| Extension | Method |
|---|---|
| [.md](file:///b:/DEV/JARVIS/README.md), [.txt](file:///b:/DEV/JARVIS/brain/requirements.txt) | Read as UTF-8 text |
| `.pdf` | `PyMuPDF` (`fitz`) text extraction |
| `.json` | `json.dumps(data, indent=2)` |
| `.csv` | Convert to markdown table |
| `.docx` | `python-docx` paragraph text |

**Hard constraints:**
```python
SUPPORTED_EXTENSIONS = {".md", ".txt", ".pdf", ".json", ".csv", ".docx"}
EXCLUDED_FOLDERS = {"Secrets", ".git", "__pycache__", "node_modules", ".system"}
MAX_FILE_SIZE_MB = 50
```

> [!CAUTION]
> The `Secrets` exclusion **MUST** be a named constant, not a hardcoded string buried in logic. Encrypted content must **never** enter the vector store.

**New dependencies to add to [brain/requirements.txt](file:///b:/DEV/JARVIS/brain/requirements.txt):**
```
PyMuPDF==1.25.3
python-docx==1.1.2
```

**Test approach:** Add a temporary debug endpoint `GET /brain/debug/load-count` that walks the vault and returns `{"files_found": N, "secrets_skipped": true}`. Verify N matches actual vault file count. Verify no file from `/Secrets` is included. Remove the debug endpoint after testing.

**Best practice:** Return a generator/iterator of `LoadedDocument` dataclasses, not a giant list. This keeps memory usage flat for large vaults:
```python
@dataclass
class LoadedDocument:
    path: str              # vault-relative path
    content: str           # extracted text
    last_modified: str     # ISO 8601
    content_hash: str      # SHA-256 of raw file bytes
```

---

### Step 4: Text Chunker

**Goal:** Split loaded documents into token-sized chunks suitable for embedding.

**File to create:** `brain/app/chunker.py`

**Spec reference:** 05-AI §Text Chunking

**Parameters (from spec):**
- Chunk size: **512 tokens**
- Chunk overlap: **64 tokens**
- Splitter: **Recursive character text splitter** (split on `\n\n`, then `\n`, then `. `, then ` `)

**New dependency:** `tiktoken` for accurate token counting (OpenAI's tokenizer library, works locally, no API calls).

**Add to [brain/requirements.txt](file:///b:/DEV/JARVIS/brain/requirements.txt):**
```
tiktoken==0.9.0
```

**Output per chunk** (from spec):
```python
@dataclass
class Chunk:
    chunk_id: str          # deterministic: sha256(path + "|" + str(chunk_index))
    source_path: str       # e.g., "Work/project-plan.md"
    chunk_index: int       # 0-based
    total_chunks: int
    content: str
    content_hash: str      # SHA-256 of chunk content
```

**Test approach:** Feed a known ~2000-word markdown file. Expect ~8 chunks. Verify consecutive chunks share overlapping text (last 64 tokens of chunk N ≈ first 64 tokens of chunk N+1).

**Best practice:** Use `tiktoken.encoding_for_model("gpt-4")` (cl100k_base) for token counting. It's a reasonable proxy and is available offline. Don't use the Ollama tokenizer endpoint — it adds unnecessary network latency during chunking.

---

### Step 5: Embedding Pipeline

**Goal:** Convert text chunks into 768-dimensional float vectors using Ollama's embedding API.

**File to create:** `brain/app/embedder.py`

**Spec reference:** 05-AI §Embedding Pipeline

**Prerequisite:** `nomic-embed-text` must be installed:
```powershell
ollama pull nomic-embed-text
```

**Ollama embedding API:**
```
POST http://host.docker.internal:11434/api/embed
{
    "model": "nomic-embed-text",
    "input": ["chunk text here"]
}
→ {"embeddings": [[0.123, -0.456, ...]]}  # 768-dim
```

**Key parameters:**
- Model: `nomic-embed-text` (768 dimensions)
- Batch size: **32 chunks per request** (balance throughput vs memory)

**Test approach:** Embed a single known string. Assert `len(embedding) == 768`. Assert all values are floats. Measure latency (should be <1s for a single chunk).

**Best practice:** Use `httpx.AsyncClient` with connection pooling. Embed in batches of 32 — the Ollama API accepts a list of inputs in `"input"` field. Handle Ollama being temporarily unavailable (retry with exponential backoff, max 3 retries).

> [!WARNING]
> The Ollama embedding endpoint is `/api/embed` (singular), NOT `/api/embeddings` (plural). The plural form is an older API. Check the Ollama docs for your version.

---

### Step 6: ChromaDB Integration

**Goal:** Store and query vector embeddings in ChromaDB.

**File to create:** `brain/app/vector_store.py`

**Spec reference:** 05-AI §Vector Store

**New dependency:**
```
chromadb-client==0.6.3
```

> [!IMPORTANT]
> Use `chromadb-client` (the thin HTTP client), NOT the full `chromadb` package. The full package pulls in heavy dependencies and tries to run a local server. The client just talks to the existing ChromaDB container.

**Collection schema** (from spec):
```python
collection_name = "jarvis_vault"
# IDs: deterministic from path + chunk_index
# Documents: chunk content text
# Embeddings: 768-dim float vectors
# Metadata: {source_path, chunk_index, content_hash, last_modified}
```

**Operations needed:**
1. `upsert_chunks(chunks, embeddings)` — insert or update
2. `delete_by_path(source_path)` — remove all chunks for a file
3. `query(embedding, top_k, filter_paths)` — similarity search
4. `get_all_metadata()` — for incremental indexing (compare hashes)
5. `count()` — for index status

**ChromaDB client connection:**
```python
import chromadb
client = chromadb.HttpClient(host="chromadb", port=8000)
collection = client.get_or_create_collection("jarvis_vault")
```

**Test approach:** Upsert 5 test chunks with known embeddings → query for one → verify correct chunk is returned with score > 0.5. Then restart ChromaDB (`docker compose restart chromadb`) and verify data persists (named volume `jarvis-chroma-data`).

---

### Step 7: Incremental Indexer

**Goal:** Orchestrate the full indexing pipeline: load → chunk → embed → store. Support incremental updates (only re-index changed files).

**File to create:** `brain/app/indexer.py`

**Spec reference:** 05-AI §Incremental Indexing

**Logic:**
1. Walk vault files via document loader
2. For each file, compute content hash
3. Compare against ChromaDB metadata:
   - **New file** (path not in DB): chunk → embed → insert
   - **Modified file** (hash differs): delete old chunks → chunk → embed → insert
   - **Deleted file** (in DB but not on disk): delete from DB
   - **Unchanged file** (hash matches): skip
4. Track stats: `files_indexed`, `chunks_created`, `files_skipped`

**Endpoints to add to [brain/app/api.py](file:///b:/DEV/JARVIS/brain/app/api.py):**
```python
POST /brain/reindex      # Force full re-index
GET  /brain/index-status # Returns IndexStatus model
```

**Index status response** (from spec):
```json
{
    "total_files_indexed": 142,
    "total_chunks": 1847,
    "last_index_run": "2026-02-19T09:00:00Z",
    "pending_files": 3,
    "index_health": "healthy"
}
```

**Startup behavior (approved):** Run indexing in a background `asyncio.Task` on startup. The brain accepts queries immediately — they just search whatever is already indexed. `GET /brain/status` reports `"indexing": true` during this process.

**Test approach:** `curl -X POST http://brain:8001/brain/reindex` → wait → `curl http://brain:8001/brain/index-status` → verify `total_files_indexed > 0`.

**Best practice:** Log each file as it's indexed (at DEBUG level). At INFO level, log summary: "Indexed 142 files (1847 chunks) in 45s. 3 new, 2 modified, 1 deleted, 136 unchanged."

---

### Step 8: Retriever + Context Assembler

**Goal:** Given a user query, find relevant chunks from the vector store and assemble them into a prompt.

**Files to create:**
- `brain/app/retriever.py` — Query embedding + similarity search
- `brain/app/context_assembler.py` — Prompt construction with source attribution

**Spec reference:** 05-AI §Retriever, §Context Assembler

**Retrieval strategy** (from spec, in order):
1. Embed the query using the same embedding model
2. Cosine similarity search, top-K (default K=5)
3. Optional path-prefix filter (e.g., only search within `Work/`)
4. Dedup: collapse multiple chunks from the same file
5. Score threshold: discard results with similarity < 0.3

**Prompt template** (from spec):
```
You are JARVIS, a personal AI assistant. You answer questions using ONLY
the provided context from the user's personal knowledge vault. If the
context doesn't contain enough information to answer, say so clearly.

=== CONTEXT ===
[Source: Work/project-plan.md]
Sprint deadline: March 15...

[Source: Goals/2026-q1.md]
Complete backend API by end of February...
=== END CONTEXT ===

User Question: {user_query}

Answer:
```

**Context budget:** Max **2048 tokens** (configurable). Chunks added in score-descending order until budget is exhausted.

**Attachment handling** (from spec):
- If `attachments` provided → read those files directly, include in context (bypass retrieval)
- If empty → purely retrieval-based
- If both → attachments first, retrieval fills remaining budget

**Test approach:** Index the vault (Step 7), then call retriever with a query like "project deadlines". Verify returned chunks come from relevant files, scores > 0.3, no duplicates from same document.

---

### Step 9: Ollama Chat Client

**Goal:** Send a prompt to Ollama and receive a streaming response.

**File to create:** `brain/app/ollama_client.py`

**Spec reference:** 05-AI §Ollama Client

**Ollama generate API:**
```
POST http://host.docker.internal:11434/api/generate
{
    "model": "llama3",
    "prompt": "...",
    "stream": true,
    "options": {
        "temperature": 0.3,
        "num_predict": 1024
    }
}
```

When `stream: true`, Ollama returns **newline-delimited JSON** (NDJSON):
```
{"response": "Based", "done": false}
{"response": " on", "done": false}
...
{"response": "", "done": true, "eval_count": 847}
```

**Key parameters:** model=`llama3`, temperature=`0.3`, max_tokens=`1024`, stream=[true](file:///b:/DEV/JARVIS/server/tests/test_sync.py#243-261)

**Test approach:** Call with a hardcoded prompt "Say hello in one sentence." Verify streaming works, response contains a greeting, timing < 15s.

**Best practice:** Yield tokens as an `AsyncGenerator[str, None]`. This allows the API layer to forward each token to the client without buffering the entire response.

---

### Step 10: `POST /brain/ask` Endpoint

**Goal:** Wire together retriever + context assembler + Ollama client into one end-to-end endpoint.

**Modify:** [brain/app/api.py](file:///b:/DEV/JARVIS/brain/app/api.py)

**Spec reference:** 05-AI §API Endpoint POST /ask

**Flow:**
```
User query
  → Embed query (embedder.py)
  → Retrieve top-K chunks (retriever.py)
  → Load attachments if any (document_loader.py)
  → Assemble prompt (context_assembler.py)
  → Send to Ollama (ollama_client.py)
  → Stream response tokens back to caller
```

**Response format** (streamed, from spec):
```json
{"answer": "Based on your vault...", "sources": [...], "model": "llama3", "tokens_used": 847}
```

**Implementation note:** Use FastAPI's `StreamingResponse` for streaming. The final response (with sources and token count) is sent after the LLM finishes generating.

**Test approach:** `curl -N -X POST http://brain:8001/brain/ask -H 'Content-Type: application/json' -d '{"query":"What files do I have?"}'` → verify streamed response.

---

### Step 11: API Proxy on `jv-api`

**Goal:** The mobile app talks to `jv-api` only (never directly to `jv-brain`). Add proxy endpoints.

**Files to create:**
- `server/app/routers/ask.py` — Proxy endpoints
- `server/app/models/ask_models.py` — Pydantic models (can reuse brain's models)

**Modify:** [server/app/main.py](file:///b:/DEV/JARVIS/server/app/main.py) — Add `app.include_router(ask.router)`

**Modify:** [server/app/config.py](file:///b:/DEV/JARVIS/server/app/config.py) — Add `brain_url: str = "http://brain:8001"` to Settings

**Endpoints:**
```
POST /ask           → proxy to POST http://brain:8001/brain/ask (streaming)
GET  /ask/status    → checks brain health + Ollama + ChromaDB
GET  /ask/index-status → proxy to GET http://brain:8001/brain/index-status
```

**Auth:** All `/ask` endpoints require JWT (same as existing `/files` and `/sync`).

**Streaming through proxy:** Use `httpx.AsyncClient.stream()` and forward chunks via `StreamingResponse`. This is the three-hop chain: Ollama → brain → api → mobile.

**Spec reference:** 02-Backend §AI Endpoints table

> [!WARNING]
> **Do NOT add any new routes that conflict with existing `/files/*` or `/sync/*` paths.** The new routes are strictly under `/ask/*`.

**Test approach:** `curl -H "Authorization: Bearer <jwt>" http://localhost:8000/ask/status` → `{"ai_available": true}`. Then test the full ask proxy.

---

### Step 12: Mobile AI Chat UI

**Goal:** Chat interface in the Flutter app for querying the AI.

**Files to create (all under `mobile/lib/features/ai_chat/`):**

| File | Purpose |
|---|---|
| `data/ai_repository.dart` | Dio calls to `POST /ask` (streaming via `ResponseType.stream`), `GET /ask/status`, `GET /ask/index-status` |
| `data/chat_history_table.dart` | Drift table definition for local chat history |
| `presentation/ai_chat_provider.dart` | Riverpod providers: chat state, message list, loading flag, server status |
| `presentation/ai_chat_screen.dart` | Chat UI: message list, input field, streaming response display, source citations |
| `presentation/widgets/chat_bubble.dart` | Reusable message bubble (user messages vs assistant messages with source links) |
| `presentation/widgets/attachment_picker.dart` | File picker to attach vault files as context |

**Schema migration (approved):**
- Bump Drift schema from v5 → v6
- Add `chat_history` table (query TEXT, response TEXT, attachments TEXT/JSON, timestamp TEXT)
- Migration goes in [app_database.dart](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart) following existing migration pattern
- **Do NOT modify [MutationQueue](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart#30-46) or [FileCacheEntries](file:///b:/DEV/JARVIS/mobile/lib/core/storage/app_database.dart#12-28) tables**

**Streaming on Flutter side:** Use Dio with `ResponseType.stream` and parse NDJSON:
```dart
final response = await dio.post('/ask', data: request, 
    options: Options(responseType: ResponseType.stream));
await for (final chunk in response.data.stream) {
    final text = utf8.decode(chunk);
    // Parse NDJSON lines and update UI
}
```

**Offline behavior:** When server is unreachable, show "AI is offline" state. The chat history is local-only (stored in SQLite, never synced).

**Navigation:** Add a new entry point to the app's navigation (bottom nav or drawer) for "AI Chat".

**Spec reference:** 03-Mobile §AI Chat, §Local Database Schema

---

## 7. Hard Constraints (Non-Negotiable)

1. ❌ **Never modify Phase 2 files** — sync engine, conflict resolution, existing database tables
2. ❌ **Never expose `jv-brain` or ChromaDB on host ports** — internal Docker network only
3. ❌ **Never index `/Secrets` folder** — exclusion must be a named constant
4. ❌ **Never break `/files/*` or `/sync/*` routes** — all new routes are additive under `/ask/*`
5. ❌ **Never call external APIs** — all inference is local via Ollama
6. ✅ **Always test each step independently** before moving to the next
7. ✅ **Always use the spec docs** — don't invent architecture or make assumptions

---

## 8. Professional Advice & Best Practices

### RAG Pipeline Architecture
- **Keep pipeline components loosely coupled.** Each file (loader, chunker, embedder, retriever, assembler, client) should be independently testable with clear input/output contracts. Use dataclasses for data transfer, not raw dicts.
- **Make chunking parameters configurable** via [BrainSettings](file:///b:/DEV/JARVIS/brain/app/config.py#6-15). You'll want to tune chunk size and overlap after testing with real vault content.
- **Log aggressively during indexing** — it's the hardest component to debug. Log file paths, chunk counts, embedding dimensions, and ChromaDB upsert confirmations.

### Streaming Architecture
- The three-hop streaming chain (Ollama → brain → api → mobile) is the most complex part of the system. Test each hop independently before wiring them together.
- Use **NDJSON** (newline-delimited JSON) format throughout. Each line is a complete JSON object. This makes parsing trivial on every layer.
- Handle **partial reads** carefully on the Flutter side — a single TCP packet may contain a partial JSON line. Buffer until you see a newline.

### Error Handling
- Follow the **graceful degradation** principle from the spec: if Ollama is down, file operations still work. If ChromaDB is down, manual attachments still work.
- Every endpoint should have clear error responses: `503 AI_UNAVAILABLE` when Ollama is unreachable, `422` for invalid queries, [404](file:///b:/DEV/JARVIS/server/tests/test_sync.py#999-1006) for attachment files that don't exist.

### Testing Strategy
- For Steps 3–6: Write simple Python scripts that exercise each component in isolation.
- For Steps 7–10: Use `curl` against the brain's endpoints.
- For Step 11: Use `curl` with JWT auth against jv-api.
- For Step 12: Manual testing in the Flutter app. Ensure the "AI offline" state renders correctly.

### Memory Considerations
- `llama3:8b` uses ~4.6 GB VRAM on the RTX 3050 (6 GB total). During inference, VRAM will spike. Don't run other GPU-intensive tasks simultaneously.
- `nomic-embed-text` is small (~274 MB) and loads fast. Ollama automatically swaps models — only one is in memory at a time.
- ChromaDB with ~10K chunks uses < 512 MB RAM (per spec). Not a concern for 16 GB total RAM.

### Deployment Checklist Before Each Step
```
1. Ollama running? → curl http://localhost:11434/api/tags
2. OLLAMA_HOST=0.0.0.0? → netstat -an | findstr 11434 (should show 0.0.0.0)
3. Containers healthy? → docker ps
4. Brain status ok? → docker exec jv-api curl -s http://brain:8001/brain/status
```
