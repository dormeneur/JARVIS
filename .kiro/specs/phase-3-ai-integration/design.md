# Design Document: Phase 3 AI Integration

## Overview

Phase 3 AI Integration implements a complete local, offline-first retrieval-augmented generation (RAG) pipeline for the JARVIS personal knowledge OS. The system enables users to query their markdown vault using natural language while maintaining complete privacy through local-only processing.

The implementation consists of two major components:

**Phase A - Backend RAG Pipeline (jv-brain service):**
- Document loading with multi-format support (markdown, txt, pdf, json, csv, docx)
- Text chunking with recursive character splitting (512 tokens, 64 token overlap)
- Embedding generation using nomic-embed-text via Ollama
- Vector storage in ChromaDB with incremental indexing
- Context retrieval with similarity search and deduplication
- LLM inference via Ollama with streaming responses

**Phase B - API Proxy and Mobile UI:**
- API proxy layer in jv-api for authenticated access
- Flutter mobile chat interface with streaming support
- Offline state management and chat history persistence

The design builds on completed Phase 2 (sync engine) and Step 1 (Docker infrastructure) without modifying any Phase 2 files.

## Architecture

### System Component Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                          Mobile App (Flutter)                        │
│  ┌────────────────┐  ┌──────────────┐  ┌────────────────────────┐  │
│  │  AI Chat UI    │  │  Attachment  │  │  Chat History (SQLite) │  │
│  │  (Streaming)   │  │  Picker      │  │  (Local Only)          │  │
│  └────────┬───────┘  └──────┬───────┘  └────────────────────────┘  │
└───────────┼──────────────────┼──────────────────────────────────────┘
            │                  │
            │ POST /ask        │
            │ (JWT Auth)       │
            ▼                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│                        jv-api (API Proxy)                            │
│  ┌────────────────────────────────────────────────────────────────┐ │
│  │  /ask Router                                                    │ │
│  │  - JWT validation                                               │ │
│  │  - Request forwarding to brain service                          │ │
│  │  - Streaming response proxy                                     │ │
│  └────────────────────────┬───────────────────────────────────────┘ │
└───────────────────────────┼─────────────────────────────────────────┘
                            │
                            │ POST /brain/ask
                            ▼
┌─────────────────────────────────────────────────────────────────────┐
│                      jv-brain (RAG Pipeline)                         │
│                                                                      │
│  ┌──────────────┐      ┌─────────────────┐      ┌────────────────┐ │
│  │  Document    │─────►│  Text Chunker   │─────►│  Embedding     │ │
│  │  Loader      │      │  (512 tokens,   │      │  Pipeline      │ │
│  │              │      │   64 overlap)   │      │  (nomic-embed) │ │
│  └──────────────┘      └─────────────────┘      └────────┬───────┘ │
│         │                                                 │         │
│         │ Reads /JARVIS                                   │         │
│         │ (excludes Secrets/)                             │         │
│         │                                                 ▼         │
│         │                                    ┌────────────────────┐ │
│         │                                    │  Vector Store      │ │
│         │                                    │  (ChromaDB)        │ │
│         │                                    │  - 768-dim vectors │ │
│         │                                    │  - Metadata        │ │
│         │                                    │  - Incremental     │ │
│         │                                    └────────┬───────────┘ │
│         │                                             │             │
│         │                                             │             │
│  ┌──────▼──────────┐                                 │             │
│  │  Incremental    │◄────────────────────────────────┘             │
│  │  Indexer        │  (Hash comparison)                            │
│  │  - New files    │                                               │
│  │  - Modified     │                                               │
│  │  - Deleted      │                                               │
│  └─────────────────┘                                               │
│                                                                     │
│  ┌──────────────┐      ┌─────────────────┐      ┌────────────────┐│
│  │  Query       │─────►│  Retriever      │─────►│  Context       ││
│  │  Embedding   │      │  (Similarity    │      │  Assembler     ││
│  │              │      │   Search)       │      │  (2048 tokens) ││
│  └──────────────┘      └─────────────────┘      └────────┬───────┘│
│                                                            │        │
│                                                            ▼        │
│                                                   ┌────────────────┐│
│                                                   │  Ollama Client ││
│                                                   │  (llama3)      ││
│                                                   │  Streaming     ││
│                                                   └────────┬───────┘│
└─────────────────────────────────────────────────────────┼─────────┘
                                                            │
                                                            ▼
                                                   ┌────────────────┐
                                                   │  Ollama        │
                                                   │  (Native)      │
                                                   │  11434         │
                                                   └────────────────┘
```

### Data Flow

**Indexing Flow:**
1. Document Loader walks /JARVIS directory (excluding EXCLUDED_FOLDERS)
2. For each supported file, extract text content
3. Text Chunker splits content into 512-token chunks with 64-token overlap
4. Embedding Pipeline batches chunks (32 per request) and generates 768-dim vectors
5. Vector Store upserts chunks with embeddings and metadata
6. Incremental Indexer tracks file hashes to skip unchanged files

**Query Flow:**
1. User submits query via mobile app to POST /ask
2. API Proxy validates JWT and forwards to POST /brain/ask
3. Query Embedding generates vector for user query
4. Retriever performs cosine similarity search in Vector Store (top-K=5)
5. Context Assembler constructs prompt with retrieved chunks (max 2048 tokens)
6. Ollama Client sends prompt to Ollama and streams response
7. Response streamed back through API Proxy to mobile app
8. Mobile app displays streaming tokens in real-time

### Network Isolation

- jv-brain port 8001: Internal Docker network only (not exposed to host)
- ChromaDB port 8000: Internal Docker network only (not exposed to host)
- Ollama port 11434: Bound to 0.0.0.0:11434 on host (accessible via host.docker.internal)
- jv-api port 8000: Exposed to 127.0.0.1:8000 (Tailscale handles remote access)

## Components and Interfaces

### 1. Data Models (Requirement 1)

All models defined in `brain/app/models/ask_models.py`:

```python
from pydantic import BaseModel, Field
from typing import List, Optional

class AskOptions(BaseModel):
    """Configuration options for AI queries"""
    top_k: int = Field(default=5, description="Number of chunks to retrieve")
    filter_paths: List[str] = Field(default_factory=list, description="Path prefixes to filter")
    include_sources: bool = Field(default=True, description="Include source attribution")
    stream: bool = Field(default=True, description="Stream response tokens")

class AskRequest(BaseModel):
    """Request model for AI queries"""
    query: str = Field(..., description="Natural language query")
    attachments: List[str] = Field(default_factory=list, description="File paths to include as context")
    options: Optional[AskOptions] = Field(default_factory=AskOptions)

class Source(BaseModel):
    """Source attribution for retrieved context"""
    path: str = Field(..., description="Vault file path")
    chunk: int = Field(..., description="Chunk index within file")
    score: float = Field(..., description="Similarity score (0-1)")

class AskResponse(BaseModel):
    """Response model for AI queries"""
    answer: str = Field(..., description="Generated answer")
    sources: List[Source] = Field(default_factory=list, description="Source attributions")
    model: str = Field(..., description="LLM model used")
    tokens_used: int = Field(..., description="Total tokens in response")

class IndexStatus(BaseModel):
    """Status of vector index"""
    total_files_indexed: int
    total_chunks: int
    last_index_run: str  # ISO 8601 timestamp
    pending_files: int
    index_health: str  # "healthy", "indexing", "error"
```

**LoadedDocument dataclass** (internal to Document Loader):
```python
from dataclasses import dataclass
from datetime import datetime

@dataclass
class LoadedDocument:
    path: str
    content: str
    last_modified: datetime
    content_hash: str  # SHA-256 of raw bytes
```

**Chunk dataclass** (internal to Text Chunker):
```python
@dataclass
class Chunk:
    chunk_id: str  # SHA-256(source_path + "|" + chunk_index)
    source_path: str
    chunk_index: int
    total_chunks: int
    content: str
    content_hash: str  # SHA-256 of content text
```

### 2. Document Loader (Requirements 2, 21, 22)

**Module:** `brain/app/services/document_loader.py`

**Responsibilities:**
- Walk /JARVIS directory recursively
- Extract text from supported file types
- Apply exclusion rules (EXCLUDED_FOLDERS, file size limits)
- Compute content hashes for change detection
- Return generator of LoadedDocument instances

**Configuration Constants:**
```python
SUPPORTED_EXTENSIONS = [".md", ".txt", ".pdf", ".json", ".csv", ".docx"]
MAX_FILE_SIZE_MB = 50
EXCLUDED_FOLDERS = ["Secrets", ".git", "__pycache__", "node_modules", ".system"]
```

**File Type Handlers:**

| Extension | Library | Extraction Method |
|-----------|---------|-------------------|
| .md, .txt | Built-in | Read as UTF-8 text |
| .pdf | PyMuPDF (fitz) | `fitz.open(path).get_text()` |
| .docx | python-docx | Extract paragraphs and concatenate |
| .json | json | `json.dumps(json.loads(content), indent=2)` |
| .csv | csv | Convert to markdown table format |

**CSV to Markdown Conversion:**
```python
def csv_to_markdown(csv_content: str) -> str:
    """Convert CSV to markdown table"""
    reader = csv.reader(io.StringIO(csv_content))
    rows = list(reader)
    if not rows:
        return ""
    
    # Header row
    markdown = "| " + " | ".join(rows[0]) + " |\n"
    # Separator row
    markdown += "| " + " | ".join(["---"] * len(rows[0])) + " |\n"
    # Data rows
    for row in rows[1:]:
        markdown += "| " + " | ".join(row) + " |\n"
    
    return markdown
```

**Interface:**
```python
class DocumentLoader:
    def __init__(self, vault_path: str):
        self.vault_path = Path(vault_path)
    
    def load_documents(self) -> Generator[LoadedDocument, None, None]:
        """Walk vault and yield LoadedDocument instances"""
        for file_path in self._walk_vault():
            if self._should_skip(file_path):
                continue
            
            try:
                content = self._extract_content(file_path)
                yield LoadedDocument(
                    path=str(file_path.relative_to(self.vault_path)),
                    content=content,
                    last_modified=datetime.fromtimestamp(file_path.stat().st_mtime),
                    content_hash=self._compute_hash(file_path)
                )
            except Exception as e:
                logger.error(f"Failed to load {file_path}: {e}")
                continue
    
    def _should_skip(self, path: Path) -> bool:
        """Check if file should be excluded"""
        # Check folder exclusions
        for part in path.parts:
            if part in EXCLUDED_FOLDERS:
                return True
        
        # Check file size
        if path.stat().st_size > MAX_FILE_SIZE_MB * 1024 * 1024:
            return True
        
        # Check extension
        if path.suffix not in SUPPORTED_EXTENSIONS:
            return True
        
        return False
```

### 3. Text Chunker (Requirement 3)

**Module:** `brain/app/services/text_chunker.py`

**Responsibilities:**
- Split document text into 512-token chunks
- Create 64-token overlap between consecutive chunks
- Use recursive character text splitter
- Generate deterministic chunk IDs
- Compute content hashes for each chunk

**Configuration:**
```python
CHUNK_SIZE_TOKENS = 512
CHUNK_OVERLAP_TOKENS = 64
SPLIT_SEQUENCE = ["\n\n", "\n", ". ", " "]  # Try in order
ENCODING = "cl100k_base"  # tiktoken encoding
```

**Interface:**
```python
from langchain.text_splitter import RecursiveCharacterTextSplitter
import tiktoken

class TextChunker:
    def __init__(self):
        self.encoding = tiktoken.get_encoding(ENCODING)
        self.splitter = RecursiveCharacterTextSplitter(
            chunk_size=CHUNK_SIZE_TOKENS,
            chunk_overlap=CHUNK_OVERLAP_TOKENS,
            length_function=self._count_tokens,
            separators=SPLIT_SEQUENCE
        )
    
    def _count_tokens(self, text: str) -> int:
        """Count tokens using tiktoken"""
        return len(self.encoding.encode(text))
    
    def chunk_document(self, doc: LoadedDocument) -> List[Chunk]:
        """Split document into chunks"""
        texts = self.splitter.split_text(doc.content)
        chunks = []
        
        for i, text in enumerate(texts):
            chunk_id = self._generate_chunk_id(doc.path, i)
            content_hash = hashlib.sha256(text.encode()).hexdigest()
            
            chunks.append(Chunk(
                chunk_id=chunk_id,
                source_path=doc.path,
                chunk_index=i,
                total_chunks=len(texts),
                content=text,
                content_hash=content_hash
            ))
        
        return chunks
    
    def _generate_chunk_id(self, source_path: str, chunk_index: int) -> str:
        """Generate deterministic chunk ID"""
        key = f"{source_path}|{chunk_index}"
        return hashlib.sha256(key.encode()).hexdigest()
```

### 4. Embedding Pipeline (Requirement 4)

**Module:** `brain/app/services/embedding_pipeline.py`

**Responsibilities:**
- Generate 768-dimensional embeddings using nomic-embed-text
- Batch chunks for efficient processing (32 per request)
- Implement retry logic with exponential backoff
- Use connection pooling for HTTP requests

**Configuration:**
```python
EMBEDDING_MODEL = "nomic-embed-text"
BATCH_SIZE = 32
MAX_RETRIES = 3
RETRY_BACKOFF_BASE = 2  # seconds
```

**Interface:**
```python
import httpx
from tenacity import retry, stop_after_attempt, wait_exponential

class EmbeddingPipeline:
    def __init__(self, ollama_url: str):
        self.ollama_url = ollama_url
        self.client = httpx.AsyncClient(timeout=30.0)
    
    @retry(
        stop=stop_after_attempt(MAX_RETRIES),
        wait=wait_exponential(multiplier=RETRY_BACKOFF_BASE)
    )
    async def embed_batch(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings for batch of texts"""
        response = await self.client.post(
            f"{self.ollama_url}/api/embed",
            json={
                "model": EMBEDDING_MODEL,
                "input": texts
            }
        )
        response.raise_for_status()
        data = response.json()
        return data["embeddings"]
    
    async def embed_chunks(self, chunks: List[Chunk]) -> List[Tuple[Chunk, List[float]]]:
        """Embed chunks in batches"""
        results = []
        
        for i in range(0, len(chunks), BATCH_SIZE):
            batch = chunks[i:i + BATCH_SIZE]
            texts = [chunk.content for chunk in batch]
            embeddings = await self.embed_batch(texts)
            
            for chunk, embedding in zip(batch, embeddings):
                results.append((chunk, embedding))
        
        return results
    
    async def embed_query(self, query: str) -> List[float]:
        """Embed single query string"""
        embeddings = await self.embed_batch([query])
        return embeddings[0]
```

### 5. Vector Store (Requirement 5)

**Module:** `brain/app/services/vector_store.py`

**Responsibilities:**
- Interface with ChromaDB for vector storage and retrieval
- Store chunks with embeddings and metadata
- Provide CRUD operations (upsert, delete, query)
- Support filtering by path prefix

**Configuration:**
```python
COLLECTION_NAME = "jarvis_vault"
EMBEDDING_DIMENSION = 768
```

**Interface:**
```python
import chromadb
from chromadb.config import Settings

class VectorStore:
    def __init__(self, chromadb_url: str):
        self.client = chromadb.HttpClient(
            host=chromadb_url.split("://")[1].split(":")[0],
            port=int(chromadb_url.split(":")[-1])
        )
        self.collection = self.client.get_or_create_collection(
            name=COLLECTION_NAME,
            metadata={"hnsw:space": "cosine"}
        )
    
    async def upsert_chunks(
        self,
        chunks: List[Chunk],
        embeddings: List[List[float]]
    ) -> None:
        """Insert or update chunks with embeddings"""
        self.collection.upsert(
            ids=[chunk.chunk_id for chunk in chunks],
            documents=[chunk.content for chunk in chunks],
            embeddings=embeddings,
            metadatas=[
                {
                    "source_path": chunk.source_path,
                    "chunk_index": chunk.chunk_index,
                    "content_hash": chunk.content_hash,
                    "last_modified": datetime.now().isoformat()
                }
                for chunk in chunks
            ]
        )
    
    async def delete_by_path(self, source_path: str) -> None:
        """Delete all chunks for a given source path"""
        self.collection.delete(
            where={"source_path": source_path}
        )
    
    async def query(
        self,
        query_embedding: List[float],
        top_k: int = 5,
        filter_paths: Optional[List[str]] = None
    ) -> List[dict]:
        """Query for similar chunks"""
        where = None
        if filter_paths:
            # ChromaDB filter for path prefix
            where = {
                "$or": [
                    {"source_path": {"$regex": f"^{path}"}}
                    for path in filter_paths
                ]
            }
        
        results = self.collection.query(
            query_embeddings=[query_embedding],
            n_results=top_k,
            where=where
        )
        
        return [
            {
                "chunk_id": results["ids"][0][i],
                "content": results["documents"][0][i],
                "metadata": results["metadatas"][0][i],
                "score": 1 - results["distances"][0][i]  # Convert distance to similarity
            }
            for i in range(len(results["ids"][0]))
        ]
    
    async def get_all_metadata(self) -> List[dict]:
        """Retrieve all chunk metadata for indexing comparison"""
        results = self.collection.get(include=["metadatas"])
        return [
            {
                "chunk_id": results["ids"][i],
                "metadata": results["metadatas"][i]
            }
            for i in range(len(results["ids"]))
        ]
    
    async def count(self) -> int:
        """Return total number of chunks"""
        return self.collection.count()
```


### 6. Incremental Indexer (Requirement 6)

**Module:** `brain/app/services/incremental_indexer.py`

**Responsibilities:**
- Track file changes using content hash comparison
- Index only new or modified files
- Delete chunks for removed files
- Run as background task on startup
- Provide manual re-index endpoint

**Indexing Logic:**
```python
class IncrementalIndexer:
    def __init__(
        self,
        document_loader: DocumentLoader,
        text_chunker: TextChunker,
        embedding_pipeline: EmbeddingPipeline,
        vector_store: VectorStore
    ):
        self.document_loader = document_loader
        self.text_chunker = text_chunker
        self.embedding_pipeline = embedding_pipeline
        self.vector_store = vector_store
        self.stats = {
            "files_indexed": 0,
            "chunks_created": 0,
            "files_skipped": 0,
            "files_deleted": 0,
            "files_modified": 0
        }
        self.last_index_run: Optional[datetime] = None
        self.index_health = "healthy"
    
    async def run_indexing(self) -> dict:
        """Run full incremental indexing pass"""
        try:
            self.index_health = "indexing"
            self.stats = {k: 0 for k in self.stats}
            
            # Get current state from vector store
            existing_metadata = await self.vector_store.get_all_metadata()
            existing_files = self._group_by_path(existing_metadata)
            
            # Get current vault state
            vault_files = {}
            for doc in self.document_loader.load_documents():
                vault_files[doc.path] = doc
            
            # Process new and modified files
            for path, doc in vault_files.items():
                if path not in existing_files:
                    # New file
                    await self._index_file(doc)
                    self.stats["files_indexed"] += 1
                elif self._has_changed(doc, existing_files[path]):
                    # Modified file
                    await self.vector_store.delete_by_path(path)
                    await self._index_file(doc)
                    self.stats["files_modified"] += 1
                else:
                    # Unchanged file
                    self.stats["files_skipped"] += 1
            
            # Process deleted files
            for path in existing_files:
                if path not in vault_files:
                    await self.vector_store.delete_by_path(path)
                    self.stats["files_deleted"] += 1
            
            self.last_index_run = datetime.now()
            self.index_health = "healthy"
            
            logger.info(
                f"Indexing complete: {self.stats['files_indexed']} new, "
                f"{self.stats['files_modified']} modified, "
                f"{self.stats['files_deleted']} deleted, "
                f"{self.stats['files_skipped']} unchanged"
            )
            
            return self.stats
            
        except Exception as e:
            logger.error(f"Indexing failed: {e}")
            self.index_health = "error"
            raise
    
    async def _index_file(self, doc: LoadedDocument) -> None:
        """Chunk, embed, and store a single file"""
        chunks = self.text_chunker.chunk_document(doc)
        if not chunks:
            return
        
        chunk_embedding_pairs = await self.embedding_pipeline.embed_chunks(chunks)
        chunks_list = [pair[0] for pair in chunk_embedding_pairs]
        embeddings_list = [pair[1] for pair in chunk_embedding_pairs]
        
        await self.vector_store.upsert_chunks(chunks_list, embeddings_list)
        self.stats["chunks_created"] += len(chunks)
    
    def _group_by_path(self, metadata_list: List[dict]) -> dict:
        """Group chunk metadata by source path"""
        grouped = {}
        for item in metadata_list:
            path = item["metadata"]["source_path"]
            if path not in grouped:
                grouped[path] = []
            grouped[path].append(item)
        return grouped
    
    def _has_changed(self, doc: LoadedDocument, existing_chunks: List[dict]) -> bool:
        """Check if file has changed by comparing hashes"""
        if not existing_chunks:
            return True
        # Compare document hash with first chunk's metadata
        stored_hash = existing_chunks[0]["metadata"].get("content_hash")
        return doc.content_hash != stored_hash
    
    def get_status(self) -> IndexStatus:
        """Return current index status"""
        return IndexStatus(
            total_files_indexed=self.stats["files_indexed"] + self.stats["files_modified"],
            total_chunks=self.stats["chunks_created"],
            last_index_run=self.last_index_run.isoformat() if self.last_index_run else "",
            pending_files=0,  # Could track pending queue in future
            index_health=self.index_health
        )
```

### 7. Retriever (Requirement 7)

**Module:** `brain/app/services/retriever.py`

**Responsibilities:**
- Embed user query
- Perform similarity search in vector store
- Apply score threshold (0.3 minimum)
- Deduplicate results by source file
- Return top-K chunks with scores

**Configuration:**
```python
MIN_SIMILARITY_SCORE = 0.3
DEFAULT_TOP_K = 5
```

**Interface:**
```python
class Retriever:
    def __init__(
        self,
        embedding_pipeline: EmbeddingPipeline,
        vector_store: VectorStore
    ):
        self.embedding_pipeline = embedding_pipeline
        self.vector_store = vector_store
    
    async def retrieve(
        self,
        query: str,
        top_k: int = DEFAULT_TOP_K,
        filter_paths: Optional[List[str]] = None
    ) -> List[dict]:
        """Retrieve relevant chunks for query"""
        # Embed query
        query_embedding = await self.embedding_pipeline.embed_query(query)
        
        # Query vector store (request more than top_k to allow for deduplication)
        raw_results = await self.vector_store.query(
            query_embedding=query_embedding,
            top_k=top_k * 3,  # Over-fetch for deduplication
            filter_paths=filter_paths
        )
        
        # Filter by score threshold
        filtered_results = [
            r for r in raw_results
            if r["score"] >= MIN_SIMILARITY_SCORE
        ]
        
        # Deduplicate by source file (keep highest scoring chunk per file)
        deduplicated = self._deduplicate_by_source(filtered_results)
        
        # Return top-K after deduplication
        return deduplicated[:top_k]
    
    def _deduplicate_by_source(self, results: List[dict]) -> List[dict]:
        """Keep only highest-scoring chunk per source file"""
        seen_paths = {}
        deduplicated = []
        
        # Results are already sorted by score (descending)
        for result in results:
            path = result["metadata"]["source_path"]
            if path not in seen_paths:
                seen_paths[path] = True
                deduplicated.append(result)
        
        return deduplicated
```

### 8. Context Assembler (Requirement 8)

**Module:** `brain/app/services/context_assembler.py`

**Responsibilities:**
- Construct prompts with system instructions
- Include retrieved context with source attribution
- Handle attachment files (direct inclusion)
- Enforce token budget (2048 tokens max)
- Prioritize attachments over retrieved chunks

**Configuration:**
```python
MAX_CONTEXT_TOKENS = 2048
SYSTEM_PROMPT = """You are JARVIS, a personal AI assistant. You answer questions using ONLY 
the provided context from the user's personal knowledge vault. If the 
context doesn't contain enough information to answer, say so clearly."""
```

**Interface:**
```python
class ContextAssembler:
    def __init__(
        self,
        text_chunker: TextChunker,
        document_loader: DocumentLoader
    ):
        self.text_chunker = text_chunker
        self.document_loader = document_loader
    
    async def assemble_prompt(
        self,
        query: str,
        retrieved_chunks: List[dict],
        attachments: List[str]
    ) -> Tuple[str, List[Source]]:
        """Assemble final prompt with context"""
        context_parts = []
        sources = []
        token_count = 0
        
        # Add attachments first (highest priority)
        for attachment_path in attachments:
            content = await self._load_attachment(attachment_path)
            if not content:
                continue
            
            chunk_tokens = self.text_chunker._count_tokens(content)
            if token_count + chunk_tokens > MAX_CONTEXT_TOKENS:
                # Truncate if needed
                remaining_tokens = MAX_CONTEXT_TOKENS - token_count
                content = self._truncate_to_tokens(content, remaining_tokens)
                chunk_tokens = remaining_tokens
            
            context_parts.append(f"[Source: {attachment_path}]\n{content}\n")
            sources.append(Source(path=attachment_path, chunk=0, score=1.0))
            token_count += chunk_tokens
            
            if token_count >= MAX_CONTEXT_TOKENS:
                break
        
        # Add retrieved chunks to fill remaining budget
        for result in retrieved_chunks:
            if token_count >= MAX_CONTEXT_TOKENS:
                break
            
            content = result["content"]
            chunk_tokens = self.text_chunker._count_tokens(content)
            
            if token_count + chunk_tokens > MAX_CONTEXT_TOKENS:
                # Truncate if needed
                remaining_tokens = MAX_CONTEXT_TOKENS - token_count
                content = self._truncate_to_tokens(content, remaining_tokens)
                chunk_tokens = remaining_tokens
            
            path = result["metadata"]["source_path"]
            chunk_index = result["metadata"]["chunk_index"]
            score = result["score"]
            
            context_parts.append(f"[Source: {path}]\n{content}\n")
            sources.append(Source(path=path, chunk=chunk_index, score=score))
            token_count += chunk_tokens
        
        # Construct final prompt
        context_section = "\n".join(context_parts)
        prompt = f"""{SYSTEM_PROMPT}

=== CONTEXT ===
{context_section}
=== END CONTEXT ===

User Question: {query}

Answer:"""
        
        return prompt, sources
    
    async def _load_attachment(self, path: str) -> Optional[str]:
        """Load attachment file content"""
        try:
            # Use document loader to extract content
            full_path = Path(self.document_loader.vault_path) / path
            if not full_path.exists():
                logger.warning(f"Attachment not found: {path}")
                return None
            
            # Create temporary LoadedDocument
            doc = LoadedDocument(
                path=path,
                content=self.document_loader._extract_content(full_path),
                last_modified=datetime.now(),
                content_hash=""
            )
            return doc.content
        except Exception as e:
            logger.error(f"Failed to load attachment {path}: {e}")
            return None
    
    def _truncate_to_tokens(self, text: str, max_tokens: int) -> str:
        """Truncate text to fit within token budget"""
        tokens = self.text_chunker.encoding.encode(text)
        if len(tokens) <= max_tokens:
            return text
        truncated_tokens = tokens[:max_tokens]
        return self.text_chunker.encoding.decode(truncated_tokens)
```

### 9. Ollama Client (Requirement 9)

**Module:** `brain/app/services/ollama_client.py`

**Responsibilities:**
- Send prompts to Ollama /api/generate endpoint
- Parse NDJSON streaming responses
- Yield tokens as AsyncGenerator
- Extract token count from final response

**Configuration:**
```python
LLM_MODEL = "llama3"
TEMPERATURE = 0.3
MAX_TOKENS = 1024
```

**Interface:**
```python
from typing import AsyncGenerator

class OllamaClient:
    def __init__(self, ollama_url: str):
        self.ollama_url = ollama_url
        self.client = httpx.AsyncClient(timeout=120.0)
    
    async def generate_streaming(
        self,
        prompt: str
    ) -> AsyncGenerator[dict, None]:
        """Generate streaming response from Ollama"""
        request_data = {
            "model": LLM_MODEL,
            "prompt": prompt,
            "stream": True,
            "options": {
                "temperature": TEMPERATURE,
                "num_predict": MAX_TOKENS
            }
        }
        
        async with self.client.stream(
            "POST",
            f"{self.ollama_url}/api/generate",
            json=request_data
        ) as response:
            response.raise_for_status()
            
            async for line in response.aiter_lines():
                if not line.strip():
                    continue
                
                try:
                    data = json.loads(line)
                    yield data
                except json.JSONDecodeError:
                    logger.warning(f"Failed to parse NDJSON line: {line}")
                    continue
    
    async def generate(self, prompt: str) -> Tuple[str, int]:
        """Generate complete response (non-streaming)"""
        full_response = ""
        tokens_used = 0
        
        async for chunk in self.generate_streaming(prompt):
            if "response" in chunk:
                full_response += chunk["response"]
            
            if chunk.get("done", False):
                tokens_used = chunk.get("eval_count", 0)
        
        return full_response, tokens_used
```

### 10. Brain Service Endpoints (Requirements 10, 6)

**Module:** `brain/app/main.py` and `brain/app/routers/ask.py`

**Endpoints:**

**POST /brain/ask** - Main AI query endpoint
```python
from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse

@router.post("/brain/ask")
async def ask_endpoint(request: AskRequest):
    """Handle AI query with streaming response"""
    try:
        # Embed query
        query_embedding = await embedding_pipeline.embed_query(request.query)
        
        # Retrieve relevant chunks
        retrieved_chunks = await retriever.retrieve(
            query=request.query,
            top_k=request.options.top_k,
            filter_paths=request.options.filter_paths
        )
        
        # Assemble prompt with context
        prompt, sources = await context_assembler.assemble_prompt(
            query=request.query,
            retrieved_chunks=retrieved_chunks,
            attachments=request.attachments
        )
        
        # Stream response from Ollama
        if request.options.stream:
            return StreamingResponse(
                stream_response(prompt, sources),
                media_type="application/x-ndjson"
            )
        else:
            answer, tokens_used = await ollama_client.generate(prompt)
            return AskResponse(
                answer=answer,
                sources=sources if request.options.include_sources else [],
                model=LLM_MODEL,
                tokens_used=tokens_used
            )
    
    except Exception as e:
        logger.error(f"Ask endpoint failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))

async def stream_response(prompt: str, sources: List[Source]):
    """Stream NDJSON response"""
    full_answer = ""
    tokens_used = 0
    
    async for chunk in ollama_client.generate_streaming(prompt):
        if "response" in chunk:
            token = chunk["response"]
            full_answer += token
            yield json.dumps({"token": token}) + "\n"
        
        if chunk.get("done", False):
            tokens_used = chunk.get("eval_count", 0)
    
    # Send final response with metadata
    final_response = AskResponse(
        answer=full_answer,
        sources=sources,
        model=LLM_MODEL,
        tokens_used=tokens_used
    )
    yield json.dumps(final_response.dict()) + "\n"
```

**POST /brain/reindex** - Trigger manual re-indexing
```python
@router.post("/brain/reindex")
async def reindex_endpoint():
    """Trigger full incremental re-index"""
    try:
        stats = await incremental_indexer.run_indexing()
        return {
            "status": "complete",
            "stats": stats
        }
    except Exception as e:
        logger.error(f"Reindex failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))
```

**GET /brain/index-status** - Get indexing status
```python
@router.get("/brain/index-status", response_model=IndexStatus)
async def index_status_endpoint():
    """Get current index status"""
    return incremental_indexer.get_status()
```

**GET /brain/status** - Health check
```python
@router.get("/brain/status")
async def status_endpoint():
    """Check brain service health"""
    try:
        # Check Ollama
        ollama_healthy = await check_ollama_health()
        
        # Check ChromaDB
        chromadb_healthy = await check_chromadb_health()
        
        return {
            "status": "healthy" if (ollama_healthy and chromadb_healthy) else "degraded",
            "ollama": "available" if ollama_healthy else "unavailable",
            "chromadb": "available" if chromadb_healthy else "unavailable"
        }
    except Exception as e:
        return {
            "status": "error",
            "error": str(e)
        }
```

### 11. API Proxy Layer (Requirement 11)

**Module:** `server/app/routers/ask.py`

**Responsibilities:**
- Proxy AI requests from mobile to brain service
- Validate JWT authentication
- Forward streaming responses
- Provide health check aggregation

**Configuration in server/app/config.py:**
```python
class Settings(BaseSettings):
    # Existing settings...
    brain_url: str = Field(default="http://brain:8001", env="JARVIS_BRAIN_URL")
```

**Endpoints:**

**POST /ask** - Proxy to brain service
```python
from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import StreamingResponse
import httpx

router = APIRouter(prefix="/ask", tags=["ai"])

@router.post("")
async def ask_proxy(
    request: AskRequest,
    token: str = Depends(verify_jwt_token)
):
    """Proxy AI query to brain service"""
    try:
        async with httpx.AsyncClient() as client:
            async with client.stream(
                "POST",
                f"{settings.brain_url}/brain/ask",
                json=request.dict(),
                timeout=120.0
            ) as response:
                response.raise_for_status()
                
                async def forward_stream():
                    async for chunk in response.aiter_bytes():
                        yield chunk
                
                return StreamingResponse(
                    forward_stream(),
                    media_type="application/x-ndjson"
                )
    
    except httpx.HTTPError as e:
        logger.error(f"Brain service request failed: {e}")
        raise HTTPException(
            status_code=503,
            detail="AI service unavailable"
        )
```

**GET /ask/status** - Aggregate health check
```python
@router.get("/status")
async def ask_status(token: str = Depends(verify_jwt_token)):
    """Check AI service availability"""
    try:
        async with httpx.AsyncClient() as client:
            response = await client.get(
                f"{settings.brain_url}/brain/status",
                timeout=5.0
            )
            response.raise_for_status()
            brain_status = response.json()
            
            return {
                "ai_available": brain_status["status"] == "healthy",
                "details": brain_status
            }
    
    except Exception as e:
        return {
            "ai_available": False,
            "error": str(e)
        }
```

**GET /ask/index-status** - Proxy index status
```python
@router.get("/index-status")
async def index_status_proxy(token: str = Depends(verify_jwt_token)):
    """Get index status from brain service"""
    try:
        async with httpx.AsyncClient() as client:
            response = await client.get(
                f"{settings.brain_url}/brain/index-status",
                timeout=5.0
            )
            response.raise_for_status()
            return response.json()
    
    except httpx.HTTPError as e:
        raise HTTPException(
            status_code=503,
            detail="Brain service unavailable"
        )
```


### 12. Mobile AI Chat Feature (Requirement 12)

**Directory Structure:**
```
mobile/lib/features/ai_chat/
├── data/
│   ├── ai_repository.dart
│   └── chat_history_table.dart
└── presentation/
    ├── ai_chat_provider.dart
    ├── ai_chat_screen.dart
    └── widgets/
        ├── chat_bubble.dart
        └── attachment_picker.dart
```

**Drift Schema Migration (v5 → v6):**

In `mobile/lib/core/storage/app_database.dart`:
```dart
@DriftDatabase(
  tables: [
    MutationQueue,        // Phase 2 - DO NOT MODIFY
    FileCacheEntries,     // Phase 2 - DO NOT MODIFY
    ChatHistory,          // Phase 3 - NEW
  ],
  daos: [SyncDao, ChatDao],
  version: 6,
)
class AppDatabase extends _$AppDatabase {
  AppDatabase(QueryExecutor e) : super(e);
  
  @override
  int get schemaVersion => 6;
  
  @override
  MigrationStrategy get migration => MigrationStrategy(
    onUpgrade: (migrator, from, to) async {
      if (from == 5 && to == 6) {
        await migrator.createTable(chatHistory);
      }
    },
  );
}
```

**Chat History Table:**
```dart
// mobile/lib/features/ai_chat/data/chat_history_table.dart
import 'package:drift/drift.dart';

class ChatHistory extends Table {
  IntColumn get id => integer().autoIncrement()();
  TextColumn get query => text()();
  TextColumn get response => text()();
  TextColumn get attachments => text()();  // JSON array of paths
  TextColumn get timestamp => text()();    // ISO 8601
}
```

**AI Repository:**
```dart
// mobile/lib/features/ai_chat/data/ai_repository.dart
import 'package:dio/dio.dart';

class AiRepository {
  final Dio _dio;
  final String _baseUrl;
  
  AiRepository(this._dio, this._baseUrl);
  
  Stream<String> askStreaming({
    required String query,
    List<String> attachments = const [],
    int topK = 5,
  }) async* {
    try {
      final response = await _dio.post(
        '$_baseUrl/ask',
        data: {
          'query': query,
          'attachments': attachments,
          'options': {
            'top_k': topK,
            'filter_paths': [],
            'include_sources': true,
            'stream': true,
          },
        },
        options: Options(
          responseType: ResponseType.stream,
          headers: {'Accept': 'application/x-ndjson'},
        ),
      );
      
      final stream = response.data.stream;
      await for (final chunk in stream) {
        final lines = utf8.decode(chunk).split('\n');
        for (final line in lines) {
          if (line.trim().isEmpty) continue;
          
          try {
            final json = jsonDecode(line);
            if (json.containsKey('token')) {
              yield json['token'] as String;
            } else if (json.containsKey('answer')) {
              // Final response with metadata
              yield '\n\n--- Sources ---\n';
              final sources = json['sources'] as List;
              for (final source in sources) {
                yield '${source['path']} (score: ${source['score']})\n';
              }
            }
          } catch (e) {
            print('Failed to parse NDJSON line: $e');
          }
        }
      }
    } on DioException catch (e) {
      if (e.type == DioExceptionType.connectionTimeout ||
          e.type == DioExceptionType.receiveTimeout) {
        throw AiUnavailableException('AI service is offline');
      }
      rethrow;
    }
  }
  
  Future<Map<String, dynamic>> getStatus() async {
    try {
      final response = await _dio.get('$_baseUrl/ask/status');
      return response.data;
    } catch (e) {
      return {'ai_available': false, 'error': e.toString()};
    }
  }
  
  Future<Map<String, dynamic>> getIndexStatus() async {
    final response = await _dio.get('$_baseUrl/ask/index-status');
    return response.data;
  }
}

class AiUnavailableException implements Exception {
  final String message;
  AiUnavailableException(this.message);
}
```

**AI Chat Provider (Riverpod):**
```dart
// mobile/lib/features/ai_chat/presentation/ai_chat_provider.dart
import 'package:flutter_riverpod/flutter_riverpod.dart';

class ChatMessage {
  final String content;
  final bool isUser;
  final DateTime timestamp;
  final List<String> attachments;
  
  ChatMessage({
    required this.content,
    required this.isUser,
    required this.timestamp,
    this.attachments = const [],
  });
}

class ChatState {
  final List<ChatMessage> messages;
  final bool isStreaming;
  final bool aiAvailable;
  final String? error;
  
  ChatState({
    this.messages = const [],
    this.isStreaming = false,
    this.aiAvailable = true,
    this.error,
  });
  
  ChatState copyWith({
    List<ChatMessage>? messages,
    bool? isStreaming,
    bool? aiAvailable,
    String? error,
  }) {
    return ChatState(
      messages: messages ?? this.messages,
      isStreaming: isStreaming ?? this.isStreaming,
      aiAvailable: aiAvailable ?? this.aiAvailable,
      error: error ?? this.error,
    );
  }
}

class AiChatNotifier extends StateNotifier<ChatState> {
  final AiRepository _repository;
  final ChatDao _chatDao;
  
  AiChatNotifier(this._repository, this._chatDao) : super(ChatState()) {
    _checkAiStatus();
  }
  
  Future<void> _checkAiStatus() async {
    final status = await _repository.getStatus();
    state = state.copyWith(
      aiAvailable: status['ai_available'] ?? false,
    );
  }
  
  Future<void> sendMessage(String query, List<String> attachments) async {
    if (!state.aiAvailable) {
      state = state.copyWith(error: 'AI is offline');
      return;
    }
    
    // Add user message
    final userMessage = ChatMessage(
      content: query,
      isUser: true,
      timestamp: DateTime.now(),
      attachments: attachments,
    );
    state = state.copyWith(
      messages: [...state.messages, userMessage],
      isStreaming: true,
      error: null,
    );
    
    // Stream AI response
    final responseBuffer = StringBuffer();
    try {
      await for (final token in _repository.askStreaming(
        query: query,
        attachments: attachments,
      )) {
        responseBuffer.write(token);
        
        // Update streaming message
        final streamingMessage = ChatMessage(
          content: responseBuffer.toString(),
          isUser: false,
          timestamp: DateTime.now(),
        );
        state = state.copyWith(
          messages: [...state.messages.where((m) => m.isUser), streamingMessage],
        );
      }
      
      // Save to chat history
      await _chatDao.insertChatMessage(
        query: query,
        response: responseBuffer.toString(),
        attachments: jsonEncode(attachments),
      );
      
      state = state.copyWith(isStreaming: false);
      
    } catch (e) {
      state = state.copyWith(
        isStreaming: false,
        error: e.toString(),
      );
    }
  }
  
  Future<void> loadHistory() async {
    final history = await _chatDao.getAllMessages();
    final messages = history.map((h) => [
      ChatMessage(
        content: h.query,
        isUser: true,
        timestamp: DateTime.parse(h.timestamp),
        attachments: (jsonDecode(h.attachments) as List).cast<String>(),
      ),
      ChatMessage(
        content: h.response,
        isUser: false,
        timestamp: DateTime.parse(h.timestamp),
      ),
    ]).expand((pair) => pair).toList();
    
    state = state.copyWith(messages: messages);
  }
}

final aiChatProvider = StateNotifierProvider<AiChatNotifier, ChatState>((ref) {
  final repository = ref.watch(aiRepositoryProvider);
  final chatDao = ref.watch(chatDaoProvider);
  return AiChatNotifier(repository, chatDao);
});
```

**AI Chat Screen:**
```dart
// mobile/lib/features/ai_chat/presentation/ai_chat_screen.dart
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

class AiChatScreen extends ConsumerStatefulWidget {
  @override
  _AiChatScreenState createState() => _AiChatScreenState();
}

class _AiChatScreenState extends ConsumerState<AiChatScreen> {
  final _controller = TextEditingController();
  final _scrollController = ScrollController();
  List<String> _attachments = [];
  
  @override
  void initState() {
    super.initState();
    ref.read(aiChatProvider.notifier).loadHistory();
  }
  
  @override
  Widget build(BuildContext context) {
    final chatState = ref.watch(aiChatProvider);
    
    return Scaffold(
      appBar: AppBar(
        title: Text('AI Chat'),
        actions: [
          if (!chatState.aiAvailable)
            Chip(
              label: Text('Offline'),
              backgroundColor: Colors.red,
            ),
        ],
      ),
      body: Column(
        children: [
          // Messages list
          Expanded(
            child: chatState.messages.isEmpty
                ? Center(child: Text('Start a conversation'))
                : ListView.builder(
                    controller: _scrollController,
                    itemCount: chatState.messages.length,
                    itemBuilder: (context, index) {
                      final message = chatState.messages[index];
                      return ChatBubble(message: message);
                    },
                  ),
          ),
          
          // Error banner
          if (chatState.error != null)
            Container(
              color: Colors.red[100],
              padding: EdgeInsets.all(8),
              child: Text(chatState.error!),
            ),
          
          // Attachments display
          if (_attachments.isNotEmpty)
            Container(
              height: 60,
              child: ListView.builder(
                scrollDirection: Axis.horizontal,
                itemCount: _attachments.length,
                itemBuilder: (context, index) {
                  return Chip(
                    label: Text(_attachments[index]),
                    onDeleted: () {
                      setState(() {
                        _attachments.removeAt(index);
                      });
                    },
                  );
                },
              ),
            ),
          
          // Input bar
          Container(
            padding: EdgeInsets.all(8),
            child: Row(
              children: [
                IconButton(
                  icon: Icon(Icons.attach_file),
                  onPressed: chatState.aiAvailable
                      ? () async {
                          final path = await showAttachmentPicker(context);
                          if (path != null) {
                            setState(() {
                              _attachments.add(path);
                            });
                          }
                        }
                      : null,
                ),
                Expanded(
                  child: TextField(
                    controller: _controller,
                    enabled: chatState.aiAvailable && !chatState.isStreaming,
                    decoration: InputDecoration(
                      hintText: chatState.aiAvailable
                          ? 'Ask a question...'
                          : 'AI is offline',
                      border: OutlineInputBorder(),
                    ),
                    onSubmitted: (value) => _sendMessage(),
                  ),
                ),
                IconButton(
                  icon: chatState.isStreaming
                      ? CircularProgressIndicator()
                      : Icon(Icons.send),
                  onPressed: chatState.aiAvailable && !chatState.isStreaming
                      ? _sendMessage
                      : null,
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
  
  void _sendMessage() {
    final query = _controller.text.trim();
    if (query.isEmpty) return;
    
    ref.read(aiChatProvider.notifier).sendMessage(query, _attachments);
    _controller.clear();
    setState(() {
      _attachments = [];
    });
    
    // Scroll to bottom
    Future.delayed(Duration(milliseconds: 100), () {
      _scrollController.animateTo(
        _scrollController.position.maxScrollExtent,
        duration: Duration(milliseconds: 300),
        curve: Curves.easeOut,
      );
    });
  }
}
```

## Data Models

All data models are defined in the Components and Interfaces section above. Key models:

- **AskRequest**: Query input with attachments and options
- **AskResponse**: Generated answer with sources and metadata
- **AskOptions**: Configuration for retrieval and streaming
- **Source**: Source attribution with path, chunk index, and score
- **IndexStatus**: Vector index health and statistics
- **LoadedDocument**: Internal document representation with hash
- **Chunk**: Text chunk with metadata for embedding

## Correctness Properties


A property is a characteristic or behavior that should hold true across all valid executions of a system—essentially, a formal statement about what the system should do. Properties serve as the bridge between human-readable specifications and machine-verifiable correctness guarantees.

### Property Reflection

After analyzing all acceptance criteria, I identified the following redundancies:

- Properties 1.1, 1.3, 1.4, 1.5 all test model serialization round-trips → Combined into Property 1
- Properties 5.4, 5.5, 5.6, 5.7 all test vector store round-trips → Combined into Property 2
- Properties 8.1, 8.2, 8.3 all test prompt template content → Combined into Property 8
- Properties 2.7 and 16.4 both test Secrets exclusion → Combined into Property 3
- Properties 3.6 and 3.7 both test hash determinism → Combined into Property 5
- Property 7.8 is subsumed by Property 7.5 (deduplication) → Removed
- Property 8.10 is subsumed by Property 8.4 (source attribution) → Removed
- Properties 16.2 and 16.3 are covered by 2.7 → Removed

### Property 1: Model Serialization Round-Trip

For any Pydantic model (AskRequest, AskResponse, Source, IndexStatus, AskOptions), serializing to JSON and deserializing back should produce an equivalent object with all fields preserved.

**Validates: Requirements 1.1, 1.3, 1.4, 1.5**

### Property 2: Vector Store Round-Trip

For any chunk with embedding and metadata, upserting to the vector store and then retrieving by ID should return the same chunk content, embedding dimensions, and metadata fields (source_path, chunk_index, content_hash).

**Validates: Requirements 5.4, 5.5, 5.6, 5.7**

### Property 3: Secrets Folder Exclusion

For any file path containing "Secrets", ".git", "__pycache__", "node_modules", or ".system" in any path component, the Document Loader should exclude it from loading, and the Vector Store should never contain chunks with source_path containing these folders.

**Validates: Requirements 2.7, 16.4**

### Property 4: File Size Limit Enforcement

For any file larger than 50 megabytes, the Document Loader should skip it and not return a LoadedDocument for that file.

**Validates: Requirement 2.8**

### Property 5: Hash Determinism

For any file or chunk, computing the SHA-256 hash multiple times should always produce the same result (idempotence).

**Validates: Requirements 2.10, 3.6, 3.7**

### Property 6: Chunk Token Size

For any document chunked by the Text Chunker, each chunk (except possibly the last) should contain approximately 512 tokens (±10% tolerance for boundary conditions).

**Validates: Requirement 3.1**

### Property 7: Chunk Overlap

For any document with multiple chunks, consecutive chunks N and N+1 should have overlapping content of approximately 64 tokens.

**Validates: Requirements 3.2, 3.9**

### Property 8: Chunk Structure

For any chunked document, each Chunk should have all required fields: chunk_id, source_path, chunk_index, total_chunks, content, and content_hash.

**Validates: Requirement 3.5**

### Property 9: Embedding Dimensions

For any text embedded by the Embedding Pipeline, the resulting vector should have exactly 768 dimensions.

**Validates: Requirement 4.4**

### Property 10: Vector Store Deletion

For any source_path, after calling delete_by_path, querying the vector store should return no chunks with that source_path.

**Validates: Requirement 5.9**

### Property 11: Vector Store Count Accuracy

For any set of N chunks upserted to the vector store, the count operation should return at least N (accounting for potential duplicates from previous operations).

**Validates: Requirement 5.12**

### Property 12: Incremental Indexing - New Files

For any file not in the Vector Store, after running incremental indexing, the Vector Store should contain chunks for that file.

**Validates: Requirement 6.4**

### Property 13: Incremental Indexing - Modified Files

For any file with a different content hash than stored in Vector Store metadata, after running incremental indexing, the Vector Store should contain only the new chunks (old chunks deleted).

**Validates: Requirement 6.5**

### Property 14: Incremental Indexing - Deleted Files

For any file that exists in Vector Store but not on disk, after running incremental indexing, the Vector Store should contain no chunks for that file.

**Validates: Requirement 6.6**

### Property 15: Incremental Indexing - Unchanged Files

For any file with the same content hash as stored in Vector Store metadata, running incremental indexing should not modify the chunks for that file (idempotence).

**Validates: Requirement 6.7**

### Property 16: Indexing Statistics Accuracy

For any indexing run, the sum of files_indexed + files_modified + files_deleted + files_skipped should equal the total number of files processed.

**Validates: Requirement 6.8**

### Property 17: Retrieval Top-K Limit

For any query with top_k parameter set to K, the Retriever should return at most K results.

**Validates: Requirement 7.3**

### Property 18: Retrieval Path Filtering

For any query with filter_paths parameter, all returned results should have source_path starting with one of the filter path prefixes.

**Validates: Requirement 7.4**

### Property 19: Retrieval Deduplication

For any query results, there should be at most one chunk per unique source_path (highest scoring chunk retained).

**Validates: Requirement 7.5**

### Property 20: Retrieval Score Threshold

For any query results, all returned chunks should have similarity score >= 0.3.

**Validates: Requirement 7.6**

### Property 21: Prompt Template Content

For any assembled prompt, it should contain: (1) system identification as "JARVIS", (2) instruction to use only provided context, and (3) instruction to state when context is insufficient.

**Validates: Requirements 8.1, 8.2, 8.3**

### Property 22: Context Source Attribution

For any assembled prompt with context chunks, each chunk should be preceded by a source attribution line showing the file path.

**Validates: Requirement 8.4**

### Property 23: Context Token Budget

For any assembled prompt, the context section should not exceed 2048 tokens.

**Validates: Requirement 8.5**

### Property 24: Context Chunk Ordering

For any assembled prompt with multiple retrieved chunks, the chunks should appear in descending order by similarity score.

**Validates: Requirement 8.6**

### Property 25: Attachment Inclusion

For any attachments specified in the request, the assembled prompt should include the content of those files in the context section.

**Validates: Requirement 8.7**

### Property 26: Attachment Priority

For any prompt with both attachments and retrieved chunks, attachments should appear in the context section before retrieved chunks.

**Validates: Requirement 8.9**

### Property 27: NDJSON Parsing

For any valid newline-delimited JSON stream, the Ollama Client should successfully parse each line without errors.

**Validates: Requirements 9.6, 12.13**

### Property 28: Token Count Extraction

For any complete Ollama response (where done=true), the client should extract and return the eval_count value as tokens_used.

**Validates: Requirement 9.8**

### Property 29: Streaming Response Format

For any query to the Brain Service, the response should be in NDJSON format with streaming tokens.

**Validates: Requirement 10.7**

### Property 30: Complete Response Structure

For any completed query, the final AskResponse should include answer, sources list, model name, and tokens_used count.

**Validates: Requirement 10.8**

### Property 31: JWT Authentication Requirement

For any request to /ask endpoints without a valid JWT token, the API Proxy should reject the request with 401 Unauthorized.

**Validates: Requirement 11.5**

### Property 32: Response Forwarding

For any streaming response from Brain Service, the API Proxy should forward all chunks to the client without modification.

**Validates: Requirement 11.7**

### Property 33: Chat History Round-Trip

For any chat message stored in SQLite, retrieving it should return the same query, response, attachments, and timestamp.

**Validates: Requirement 12.17**

### Property 34: JSON Round-Trip

For any valid JSON file, loading and formatting should produce text that can be parsed back to an equivalent JSON structure.

**Validates: Requirement 21.4**

### Property 35: CSV Structure Preservation

For any valid CSV file, converting to markdown table should preserve the number of rows and columns from the original CSV.

**Validates: Requirement 22.4**

### Property 36: Supported File Types

For any file with extension in [.md, .txt, .pdf, .json, .csv, .docx], the Document Loader should successfully extract text content without errors.

**Validates: Requirement 2.1**

### Property 37: UTF-8 Text Preservation

For any markdown or txt file with valid UTF-8 content, loading should preserve all characters exactly.

**Validates: Requirement 2.6**

### Property 38: LoadedDocument Structure

For any loaded document, it should have all required fields: path, content, last_modified, and content_hash.

**Validates: Requirement 2.9**

### Property 39: IndexStatus Structure

For any index status response, it should include all required fields: total_files_indexed, total_chunks, last_index_run, pending_files, and index_health.

**Validates: Requirement 6.11**

## Error Handling

### Graceful Degradation Strategy

The system is designed to degrade gracefully when components are unavailable:

| Component Failure | Impact | Handling |
|-------------------|--------|----------|
| Ollama unavailable | AI queries fail | Return 503 AI_UNAVAILABLE; file operations unaffected |
| ChromaDB unavailable | Retrieval fails | Return 503 with error message; allow attachment-only queries |
| Embedding model missing | Cannot index or query | Startup check; log error and disable AI features |
| Document load failure | Single file skipped | Log error; continue processing other files |
| Chunk embedding failure | Retry with backoff | Retry up to 3 times; skip file if all retries fail |
| Invalid file format | File skipped | Log warning; continue processing |

### Error Response Codes

| Code | HTTP Status | Description | Example |
|------|-------------|-------------|---------|
| `AI_UNAVAILABLE` | 503 | Ollama or ChromaDB unreachable | "AI service is temporarily unavailable" |
| `VALIDATION_ERROR` | 422 | Invalid request parameters | "Query string cannot be empty" |
| `FILE_NOT_FOUND` | 404 | Attachment file doesn't exist | "Attachment path not found: Work/missing.md" |
| `AUTH_INVALID` | 401 | Missing or invalid JWT | "Authentication required" |
| `CONTEXT_TOO_LARGE` | 400 | Attachments exceed token budget | "Attachments exceed 2048 token limit" |

### Retry Logic

**Embedding Pipeline:**
- Retry failed requests with exponential backoff
- Base delay: 2 seconds
- Max retries: 3
- Backoff multiplier: 2x (2s, 4s, 8s)
- After 3 failures: Log error and skip file

**Vector Store Operations:**
- No automatic retry (ChromaDB client handles connection pooling)
- Connection timeout: 30 seconds
- On timeout: Raise exception to caller

**Ollama Client:**
- No automatic retry for generation (user can retry manually)
- Connection timeout: 120 seconds
- On timeout: Return error to user

### Error Logging

All errors logged with structured format:
```python
logger.error(
    "Component operation failed",
    extra={
        "component": "document_loader",
        "operation": "extract_pdf",
        "file_path": "Work/document.pdf",
        "error": str(e),
        "timestamp": datetime.now().isoformat()
    }
)
```

## Testing Strategy

### Dual Testing Approach

The implementation requires both unit tests and property-based tests for comprehensive coverage:

**Unit Tests:**
- Specific examples demonstrating correct behavior
- Edge cases (empty files, malformed input, boundary conditions)
- Error conditions (missing files, invalid formats, service unavailable)
- Integration points between components

**Property-Based Tests:**
- Universal properties that hold for all inputs
- Comprehensive input coverage through randomization
- Minimum 100 iterations per property test
- Each test tagged with reference to design property

### Property-Based Testing Configuration

**Library Selection:**
- Python: `hypothesis` library (mature, well-documented)
- Dart/Flutter: `test` package with custom generators

**Test Configuration:**
```python
from hypothesis import given, settings
import hypothesis.strategies as st

@settings(max_examples=100)
@given(
    content=st.text(min_size=1, max_size=10000),
    path=st.text(min_size=1, max_size=255)
)
def test_property_1_model_serialization(content, path):
    """
    Feature: phase-3-ai-integration, Property 1: Model Serialization Round-Trip
    
    For any Pydantic model, serializing to JSON and deserializing back
    should produce an equivalent object.
    """
    # Test implementation
    pass
```

**Test Tagging Format:**
```python
"""
Feature: phase-3-ai-integration, Property {number}: {property_text}
"""
```

### Component Testing Strategy

**Document Loader:**
- Unit tests: One test per file type with sample files
- Property tests: Property 3 (exclusions), Property 4 (size limits), Property 36 (supported types)
- Edge cases: Empty files, corrupted PDFs, invalid JSON/CSV

**Text Chunker:**
- Unit tests: 2000-word document produces ~8 chunks
- Property tests: Property 6 (token size), Property 7 (overlap), Property 8 (structure)
- Edge cases: Very short documents (< 512 tokens), documents with no natural boundaries

**Embedding Pipeline:**
- Unit tests: Single string embedding returns 768 dimensions
- Property tests: Property 9 (dimensions)
- Edge cases: Empty string, very long text (> model context)

**Vector Store:**
- Unit tests: Upsert 5 chunks, query, verify retrieval
- Property tests: Property 2 (round-trip), Property 10 (deletion), Property 11 (count)
- Edge cases: Empty collection, duplicate IDs, invalid embeddings

**Incremental Indexer:**
- Unit tests: Index new file, modify file, delete file scenarios
- Property tests: Property 12-16 (indexing behaviors), Property 16 (statistics)
- Edge cases: Empty vault, all files excluded, concurrent indexing

**Retriever:**
- Unit tests: Query "project deadlines" returns relevant chunks
- Property tests: Property 17-20 (top-K, filtering, deduplication, threshold)
- Edge cases: No results, all results below threshold, filter matches nothing

**Context Assembler:**
- Unit tests: Attachments-only, retrieved-only, mixed scenarios
- Property tests: Property 21-26 (template, attribution, budget, ordering, attachments)
- Edge cases: Context exceeds budget, no context available, very large attachments

**Ollama Client:**
- Unit tests: "Say hello" prompt returns greeting
- Property tests: Property 27 (NDJSON parsing), Property 28 (token count)
- Edge cases: Malformed NDJSON, connection timeout, empty response

**Brain Service Endpoints:**
- Unit tests: POST /brain/ask with sample query
- Property tests: Property 29-30 (response format, structure)
- Integration tests: End-to-end query flow with all components

**API Proxy:**
- Unit tests: Valid JWT forwards request, invalid JWT rejects
- Property tests: Property 31 (auth), Property 32 (forwarding)
- Edge cases: Brain service down, malformed JWT, expired token

**Mobile AI Chat:**
- Unit tests: Display offline state when server unreachable
- Property tests: Property 33 (chat history round-trip)
- Widget tests: Chat bubble rendering, attachment picker

### Verification Commands

**Step 2 (Models):**
```bash
python3 -c "from brain.app.models.ask_models import *; print(AskRequest(query='test').json())"
```

**Step 3 (Document Loader):**
```bash
curl http://localhost:8001/brain/debug/file-count
curl http://localhost:8001/brain/debug/exclusions
```

**Step 4 (Text Chunker):**
```bash
python3 -c "from brain.app.services.text_chunker import TextChunker; print(len(TextChunker().chunk_document(doc)))"
```

**Step 5 (Embedding):**
```bash
python3 -c "from brain.app.services.embedding_pipeline import EmbeddingPipeline; import asyncio; print(len(asyncio.run(EmbeddingPipeline('http://localhost:11434').embed_query('test'))))"
```

**Step 6 (Vector Store):**
```bash
python3 -c "from brain.app.services.vector_store import VectorStore; import asyncio; print(asyncio.run(VectorStore('http://localhost:8000').count()))"
```

**Step 7 (Indexing):**
```bash
curl -X POST http://localhost:8001/brain/reindex
curl http://localhost:8001/brain/index-status
```

**Step 8 (Retrieval):**
```bash
curl -X POST http://localhost:8001/brain/debug/retrieve -H "Content-Type: application/json" -d '{"query":"project deadlines"}'
```

**Step 10 (End-to-End):**
```bash
curl -X POST http://localhost:8001/brain/ask -H "Content-Type: application/json" -d '{"query":"What are my goals?"}'
```

**Step 11 (API Proxy):**
```bash
curl -H "Authorization: Bearer $JWT_TOKEN" http://localhost:8000/ask/status
curl -X POST -H "Authorization: Bearer $JWT_TOKEN" -H "Content-Type: application/json" -d '{"query":"test"}' http://localhost:8000/ask
```

**Step 12 (Mobile):**
- Manual testing: Launch app, navigate to AI Chat, verify offline state when server down
- Manual testing: Submit query, verify streaming response display
- Manual testing: Add attachment, verify it appears in context

### Test Coverage Goals

- Unit test coverage: > 80% for all service modules
- Property test coverage: All 39 properties implemented
- Integration test coverage: End-to-end flows for indexing and querying
- Edge case coverage: At least 2 edge cases per component


## Implementation Notes

### Phase 2 Isolation (Requirement 15)

The following Phase 2 files MUST NOT be modified:

**Mobile:**
- `mobile/lib/features/sync/data/sync_repository.dart`
- `mobile/lib/core/storage/app_database.dart` (MutationQueue and FileCacheEntries tables)
- `mobile/lib/features/sync/presentation/conflict_detail_screen.dart`
- `mobile/lib/features/sync/presentation/conflict_list_screen.dart`
- `mobile/lib/features/sync/presentation/conflict_provider.dart`

**Backend:**
- `server/app/services/sync.py`
- `server/app/routers/sync.py`
- `server/app/routers/files.py` (existing routes)

**Allowed Modifications:**
- Adding new router in `server/app/routers/ask.py` (new file)
- Adding new configuration in `server/app/config.py` (append only)
- Registering new router in `server/app/main.py` (append only)
- Adding new table in `mobile/lib/core/storage/app_database.dart` (schema v6)
- Creating new feature directory `mobile/lib/features/ai_chat/` (new directory)

### Environment Configuration (Requirement 13)

All AI-related configuration must be in environment variables:

**.env.template additions:**
```bash
# AI Configuration
JARVIS_OLLAMA_URL=http://host.docker.internal:11434
JARVIS_VECTORDB_URL=http://chromadb:8000
JARVIS_BRAIN_URL=http://brain:8001
JARVIS_EMBEDDING_MODEL=nomic-embed-text
JARVIS_LLM_MODEL=llama3
```

**Configuration loading in brain/app/config.py:**
```python
from pydantic_settings import BaseSettings

class Settings(BaseSettings):
    vault_path: str = Field(default="/data/JARVIS", env="JARVIS_VAULT_PATH")
    ollama_url: str = Field(default="http://host.docker.internal:11434", env="JARVIS_OLLAMA_URL")
    vectordb_url: str = Field(default="http://chromadb:8000", env="JARVIS_VECTORDB_URL")
    embedding_model: str = Field(default="nomic-embed-text", env="JARVIS_EMBEDDING_MODEL")
    llm_model: str = Field(default="llama3", env="JARVIS_LLM_MODEL")
    
    class Config:
        env_file = ".env"
```

### Dependency Management (Requirement 14)

**brain/requirements.txt additions:**
```
fastapi==0.115.0
uvicorn==0.32.0
pydantic==2.10.0
pydantic-settings==2.6.0
httpx==0.28.0
PyMuPDF==1.25.3
python-docx==1.1.2
tiktoken==0.9.0
chromadb-client==0.6.3
langchain-text-splitters==0.3.0
tenacity==9.0.0
```

**mobile/pubspec.yaml additions:**
```yaml
dependencies:
  dio: ^5.4.0
  drift: ^2.14.0
  sqlite3_flutter_libs: ^0.5.0
  flutter_riverpod: ^2.4.0
  flutter_secure_storage: ^9.0.0
```

### Network Configuration (Requirements 18, 19)

**docker-compose.yml configuration:**
```yaml
services:
  brain:
    build: ./brain
    container_name: jv-brain
    # Port 8001 NOT exposed to host (internal only)
    volumes:
      - ${JARVIS_HOST_PATH}:/data/JARVIS:ro  # Read-only
    env_file: .env
    networks:
      - jarvis-internal
    depends_on:
      - chromadb
    restart: unless-stopped
  
  chromadb:
    image: chromadb/chroma:0.6.3
    container_name: jv-chromadb
    # Port 8000 NOT exposed to host (internal only)
    volumes:
      - jarvis-chroma-data:/chroma/chroma
    networks:
      - jarvis-internal
    restart: unless-stopped
  
  api:
    build: ./server
    container_name: jv-api
    ports:
      - "127.0.0.1:8000:8000"
    volumes:
      - ${JARVIS_HOST_PATH}:/data/JARVIS
    env_file: .env
    networks:
      - jarvis-internal
    depends_on:
      - brain
    restart: unless-stopped

networks:
  jarvis-internal:
    driver: bridge

volumes:
  jarvis-chroma-data:
```

**Ollama host binding (Requirement 19):**
```bash
# Verify Ollama is bound to all interfaces
netstat -an | grep 11434
# Should show: 0.0.0.0:11434

# If not, configure Ollama to bind to all interfaces
# On Linux: Edit /etc/systemd/system/ollama.service
# Add: Environment="OLLAMA_HOST=0.0.0.0:11434"
# Then: systemctl daemon-reload && systemctl restart ollama
```

### Model Installation (Requirement 17)

Before starting implementation, ensure models are installed:

```bash
# Pull embedding model
ollama pull nomic-embed-text
# Expected size: ~274 MB

# Pull LLM model
ollama pull llama3
# Expected size: ~4.7 GB

# Verify installation
ollama list
# Should show both models
```

### Security Implementation (Requirement 16)

**EXCLUDED_FOLDERS constant (CRITICAL):**
```python
# brain/app/services/document_loader.py
EXCLUDED_FOLDERS = ["Secrets", ".git", "__pycache__", "node_modules", ".system"]

# This constant MUST be used in all path checking logic
# Never hardcode folder names in exclusion checks
```

**Security checklist:**
- [ ] EXCLUDED_FOLDERS constant defined and used consistently
- [ ] Document Loader skips all excluded folders
- [ ] Vector Store never contains Secrets/ paths
- [ ] No external API calls from Brain Service
- [ ] Ollama and ChromaDB URLs point to local instances only
- [ ] Vault mounted read-only in brain container
- [ ] JWT validation on all API proxy endpoints

### Background Indexing

**Startup behavior:**
```python
# brain/app/main.py
from contextlib import asynccontextmanager

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: Launch background indexing
    indexing_task = asyncio.create_task(
        incremental_indexer.run_indexing()
    )
    
    yield
    
    # Shutdown: Cancel background task
    indexing_task.cancel()
    try:
        await indexing_task
    except asyncio.CancelledError:
        pass

app = FastAPI(lifespan=lifespan)
```

**Non-blocking queries:**
- Indexing runs in background asyncio.Task
- Query endpoints use existing indexed content immediately
- No waiting for indexing to complete
- Manual re-index via POST /brain/reindex if needed

### Streaming Implementation

**NDJSON format:**
```
{"token": "Based"}\n
{"token": " on"}\n
{"token": " your"}\n
{"token": " vault"}\n
{"answer": "Based on your vault...", "sources": [...], "model": "llama3", "tokens_used": 847}\n
```

**FastAPI streaming:**
```python
from fastapi.responses import StreamingResponse

async def stream_generator():
    async for chunk in ollama_client.generate_streaming(prompt):
        yield json.dumps({"token": chunk["response"]}) + "\n"
    
    yield json.dumps(final_response.dict()) + "\n"

return StreamingResponse(
    stream_generator(),
    media_type="application/x-ndjson"
)
```

**Dio streaming (Flutter):**
```dart
final response = await dio.post(
  '/ask',
  data: request,
  options: Options(responseType: ResponseType.stream),
);

await for (final chunk in response.data.stream) {
  final lines = utf8.decode(chunk).split('\n');
  for (final line in lines) {
    if (line.trim().isEmpty) continue;
    final json = jsonDecode(line);
    // Process token or final response
  }
}
```

### Performance Considerations

**Indexing Performance:**
- Target: ~100 files/minute
- Batching: 32 chunks per embedding request
- Parallel processing: Process multiple files concurrently (asyncio.gather)
- Incremental: Only process changed files

**Query Performance:**
- Target: < 500ms for retrieval (embedding + vector search)
- Target: < 15s for full response (including LLM generation)
- Connection pooling: Reuse HTTP connections to Ollama
- Caching: Consider caching query embeddings for repeated queries

**Memory Management:**
- Generator pattern for document loading (avoid loading all files at once)
- Streaming responses (avoid buffering full LLM output)
- Batch size limits (32 chunks max per embedding request)
- Vector store memory: < 512MB for ~10K chunks

### Debugging Endpoints

**Additional debug endpoints for development:**

```python
# brain/app/routers/debug.py (development only)

@router.get("/debug/file-count")
async def debug_file_count():
    """Count files that would be loaded"""
    count = sum(1 for _ in document_loader.load_documents())
    return {"file_count": count}

@router.get("/debug/exclusions")
async def debug_exclusions():
    """Verify Secrets folder is excluded"""
    docs = list(document_loader.load_documents())
    secrets_files = [d.path for d in docs if "Secrets" in d.path]
    return {
        "secrets_files_found": len(secrets_files),
        "exclusion_working": len(secrets_files) == 0
    }

@router.post("/debug/retrieve")
async def debug_retrieve(query: str):
    """Test retrieval without LLM"""
    chunks = await retriever.retrieve(query)
    return {"chunks": chunks}
```

### Migration Path

**Drift schema migration (mobile):**
```dart
// mobile/lib/core/storage/app_database.dart

@override
MigrationStrategy get migration => MigrationStrategy(
  onUpgrade: (migrator, from, to) async {
    if (from == 5 && to == 6) {
      // Add chat_history table
      await migrator.createTable(chatHistory);
    }
  },
  beforeOpen: (details) async {
    if (details.wasCreated) {
      // Fresh install - create all tables
    }
  },
);
```

**Rollback strategy:**
- Vector store data is in Docker volume (can be deleted and rebuilt)
- Chat history is local only (no server sync)
- No Phase 2 files modified (rollback is safe)
- Re-indexing can be triggered manually if needed

## Appendix: File Structure

### Backend (jv-brain)

```
brain/
├── app/
│   ├── __init__.py
│   ├── main.py                      # FastAPI app, lifespan, router registration
│   ├── config.py                    # Settings from environment
│   ├── models/
│   │   ├── __init__.py
│   │   └── ask_models.py            # AskRequest, AskResponse, Source, IndexStatus
│   ├── services/
│   │   ├── __init__.py
│   │   ├── document_loader.py       # LoadedDocument, file extraction
│   │   ├── text_chunker.py          # Chunk, recursive splitting
│   │   ├── embedding_pipeline.py    # Ollama embedding client
│   │   ├── vector_store.py          # ChromaDB wrapper
│   │   ├── incremental_indexer.py   # Hash comparison, indexing logic
│   │   ├── retriever.py             # Similarity search, deduplication
│   │   ├── context_assembler.py     # Prompt construction
│   │   └── ollama_client.py         # LLM inference, streaming
│   └── routers/
│       ├── __init__.py
│       ├── ask.py                   # POST /brain/ask, GET /brain/status
│       └── debug.py                 # Debug endpoints (optional)
├── tests/
│   ├── test_document_loader.py
│   ├── test_text_chunker.py
│   ├── test_embedding_pipeline.py
│   ├── test_vector_store.py
│   ├── test_incremental_indexer.py
│   ├── test_retriever.py
│   ├── test_context_assembler.py
│   ├── test_ollama_client.py
│   └── test_integration.py
├── Dockerfile
└── requirements.txt
```

### Backend (jv-api additions)

```
server/
├── app/
│   ├── config.py                    # Add brain_url setting
│   ├── main.py                      # Register ask router
│   └── routers/
│       └── ask.py                   # NEW: POST /ask, GET /ask/status, GET /ask/index-status
```

### Mobile (Flutter)

```
mobile/lib/
├── core/
│   └── storage/
│       └── app_database.dart        # Add ChatHistory table, bump to v6
└── features/
    └── ai_chat/                     # NEW FEATURE
        ├── data/
        │   ├── ai_repository.dart   # Dio calls to /ask endpoints
        │   └── chat_history_table.dart  # Drift table definition
        └── presentation/
            ├── ai_chat_provider.dart    # Riverpod state management
            ├── ai_chat_screen.dart      # Main chat UI
            └── widgets/
                ├── chat_bubble.dart     # Message rendering
                └── attachment_picker.dart  # File selection
```

## Summary

This design document provides a comprehensive technical specification for implementing Phase 3 AI Integration. The system implements a complete RAG pipeline with:

- **8 core components**: Document Loader, Text Chunker, Embedding Pipeline, Vector Store, Incremental Indexer, Retriever, Context Assembler, Ollama Client
- **39 correctness properties**: Covering all testable acceptance criteria with property-based testing
- **Dual testing strategy**: Unit tests for examples and edge cases, property tests for universal behaviors
- **Security-first design**: Secrets folder exclusion, local-only processing, JWT authentication
- **Graceful degradation**: System remains functional when AI services are unavailable
- **Phase 2 isolation**: No modifications to existing sync engine files

The implementation follows clean architecture principles with clear separation between data models, business logic, and API layers. All components are designed to be independently testable and composable.

