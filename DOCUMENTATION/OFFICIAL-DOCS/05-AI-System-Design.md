# 05 — AI System Design

## Scope

Architecture for `jv-brain` — the local AI retrieval and reasoning pipeline. Covers document ingestion, embedding, vector storage, retrieval-augmented generation (RAG), and the Ollama integration.

---

## Design Goals

1. **Fully local**: No external API calls; all inference on-device via Ollama
2. **File-aware**: AI reasons over the user's structured vault (`/JARVIS`)
3. **Retrieval-augmented**: Context pulled from vector index, not brute-force
4. **Incrementally updated**: New/changed files indexed without full re-index
5. **Gracefully degradable**: AI unavailability doesn't break file operations

---

## System Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        jv-brain                              │
│                                                              │
│  ┌──────────┐    ┌──────────────┐    ┌──────────────────┐   │
│  │ Document  │───►│  Embedding   │───►│   Vector Store   │   │
│  │ Loader    │    │  Pipeline    │    │  (Chroma/pgvec)  │   │
│  └──────────┘    └──────────────┘    └────────┬─────────┘   │
│                                               │              │
│  ┌──────────┐    ┌──────────────┐    ┌────────▼─────────┐   │
│  │  Query   │───►│  Retriever   │───►│   Context        │   │
│  │  Router  │    │              │    │   Assembler      │   │
│  └──────────┘    └──────────────┘    └────────┬─────────┘   │
│                                               │              │
│                                      ┌────────▼─────────┐   │
│                                      │   Ollama Client   │   │
│                                      │   (LLM Inference) │   │
│                                      └──────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

---

## Pipeline Components

### 1. Document Loader

Responsible for reading vault files and extracting text content.

| File Type | Extraction Method |
|---|---|
| `.md` | Parse raw markdown text |
| `.txt` | Read as plain text |
| `.pdf` | `PyMuPDF` or `pdfplumber` text extraction |
| `.json` | Pretty-print JSON as structured text |
| `.csv` | Convert to markdown table |
| `.docx` | `python-docx` text extraction |
| Images | Skip for MVP; optional: BLIP captioning (future) |
| Binary | Skip (not indexed) |

#### Loader Configuration
```python
SUPPORTED_EXTENSIONS = [".md", ".txt", ".pdf", ".json", ".csv", ".docx"]
MAX_FILE_SIZE_MB = 50  # Skip files larger than this
EXCLUDED_FOLDERS = ["Secrets", ".git", "__pycache__", "node_modules"]
```

> [!IMPORTANT]
> The `/Secrets` folder is **never indexed**. Encrypted content should not enter the vector store.

### 2. Text Chunking

Split documents into manageable chunks for embedding.

| Parameter | Default | Rationale |
|---|---|---|
| Chunk size | 512 tokens | Fits within embedding model context |
| Chunk overlap | 64 tokens | Preserves cross-boundary context |
| Splitter type | Recursive character text splitter | Respects paragraph/sentence boundaries |

Each chunk is stored with metadata:
```json
{
  "chunk_id": "uuid",
  "source_path": "Work/project-plan.md",
  "chunk_index": 3,
  "total_chunks": 12,
  "content": "...",
  "content_hash": "sha256:...",
  "created_at": "2026-02-19T10:00:00Z"
}
```

### 3. Embedding Pipeline

| Component | Choice | Rationale |
|---|---|---|
| Embedding model | `nomic-embed-text` via Ollama | Runs locally; good quality for RAG |
| Embedding dimension | 768 | Standard for nomic-embed-text |
| Batch size | 32 documents | Balance throughput vs memory |

#### Embedding Flow
```
File content → Chunking → Batch embedding → Store in Vector DB
                                                    │
                                   Metadata stored alongside
                                   (source path, chunk index)
```

### 4. Vector Store

| Option | Pros | Cons |
|---|---|---|
| **ChromaDB** (recommended for MVP) | Simple setup, Python-native, lightweight | Single-node only |
| **PostgreSQL + pgvector** | Full SQL, scalable, robust | Heavier infrastructure |

#### MVP Decision: **ChromaDB**
- Runs as a single Docker container
- Persistent storage via Docker volume
- REST API for queries
- Can migrate to pgvector later if needed

#### Collection Schema
```
Collection: "jarvis_vault"
  - Documents: chunk content
  - Embeddings: 768-dim float vectors
  - Metadata: {source_path, chunk_index, content_hash, last_modified}
  - IDs: deterministic from (path + chunk_index)
```

### 5. Retriever

Takes a user query and returns relevant context from the vector store.

#### Retrieval Strategy
1. **Embed the query** using the same embedding model
2. **Similarity search**: Cosine similarity, top-K results (default K=5)
3. **Filter**: Optional path-prefix filter (e.g., only search within `/Work`)
4. **Dedup**: Remove chunks from the same document if redundant
5. **Score threshold**: Discard results with similarity < 0.3

#### Retrieval Request
```json
{
  "query": "What are my current project deadlines?",
  "top_k": 5,
  "filter_paths": ["Work"],
  "min_score": 0.3
}
```

#### Retrieval Response
```json
{
  "results": [
    {
      "source_path": "Work/project-plan.md",
      "chunk_index": 3,
      "content": "Sprint deadline: March 15...",
      "score": 0.87
    }
  ]
}
```

### 6. Context Assembler

Constructs the final prompt sent to Ollama.

#### Prompt Template
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

#### Context Budget
- **Max context tokens**: 2048 (configurable)
- Chunks added in score-descending order until budget exhausted
- File source attribution preserved for transparency

### 7. Ollama Client

Interfaces with the Ollama HTTP API for inference.

| Parameter | Default | Description |
|---|---|---|
| Model | `llama3` | Primary inference model |
| Temperature | 0.3 | Lower = more factual |
| Max tokens | 1024 | Maximum response length |
| Stream | `true` | Stream tokens for responsiveness |
| Endpoint | `http://ollama:11434/api/generate` | Docker internal URL |

---

## API Endpoint: `POST /ask`

### Request
```json
{
  "query": "What are my goals for Q1 2026?",
  "attachments": ["Goals/2026-q1.md"],
  "options": {
    "top_k": 5,
    "filter_paths": [],
    "include_sources": true,
    "stream": true
  }
}
```

### Response (streamed)
```json
{
  "answer": "Based on your vault, your Q1 2026 goals include...",
  "sources": [
    {"path": "Goals/2026-q1.md", "chunk": 1, "score": 0.92},
    {"path": "Personal/priorities.md", "chunk": 4, "score": 0.78}
  ],
  "model": "llama3",
  "tokens_used": 847
}
```

### Attachment Handling
- If `attachments` provided → those files are **always** included in context (bypass retrieval)
- If `attachments` empty → purely retrieval-based context
- If both → attachments included first, then top-K retrieval fills remaining budget

---

## Incremental Indexing

### File Watcher Strategy
- On startup: compare file hashes against vector store metadata
- **New file**: Chunk → embed → insert
- **Modified file** (hash changed): Delete old chunks → re-chunk → re-embed → insert
- **Deleted file**: Delete all chunks with that `source_path`
- **Renamed/moved**: Treated as delete + create

### Index Status Endpoint
```
GET /ask/index-status
Response: {
  "total_files_indexed": 142,
  "total_chunks": 1847,
  "last_index_run": "2026-02-19T09:00:00Z",
  "pending_files": 3,
  "index_health": "healthy"
}
```

---

## Failure Handling

| Failure | Impact | Handling |
|---|---|---|
| Ollama container down | AI queries fail | `GET /ask/status` returns `unavailable`; file ops unaffected |
| Vector DB down | Retrieval fails | Graceful error; user can still manually attach files |
| Embedding model missing | Cannot index or query | Startup check; auto-pull model if missing |
| Corrupt vector index | Bad search results | Full re-index from `/JARVIS` files (no data loss) |
| Query too long | Embedding may truncate | Warn user; truncate at default model max tokens |
| No relevant results | Poor answer quality | Respond: "I couldn't find relevant information in your vault" |

---

## Security Considerations

1. `/Secrets` folder is **excluded from indexing** — encrypted files never enter the vector store
2. AI prompts and responses are **not logged persistently** on the server
3. Vector store access is internal-only (Docker network); no external exposure
4. User query content stays local — no external API calls
5. Chat history stored on mobile device only (optional export)

---

## Performance Targets

| Metric | Target |
|---|---|
| Indexing speed | ~100 files/minute |
| Query latency (retrieval) | < 500ms |
| Query latency (full with LLM) | < 15s (depends on model/hardware) |
| Embedding batch throughput | 32 chunks/batch |
| Vector DB memory | < 512MB for ~10K chunks |

---

## Future Extensibility

- **Multi-model support**: Switch between llama3, mistral, etc. via config
- **Image understanding**: BLIP/LLaVA for image captioning → index visual content
- **Conversation memory**: Multi-turn conversations with history context
- **Scheduled digests**: "Daily briefing" generated from recent vault changes
- **Smart tagging**: Auto-generate tags/categories for new files
- **Relationship graph**: Extract entities and build a knowledge graph (Phase 4)
