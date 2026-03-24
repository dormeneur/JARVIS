# Requirements Document: Phase 3 AI Integration

## Introduction

Phase 3 AI Integration adds local, offline-first retrieval-augmented generation (RAG) capabilities to the JARVIS personal knowledge OS. This phase implements a complete AI pipeline in the `jv-brain` service, including document loading, text chunking, embedding generation, vector storage, context retrieval, and LLM inference via Ollama. The system enables users to query their markdown vault using natural language while maintaining complete privacy through local-only processing.

This phase builds on completed Phase 2 (sync engine and conflict resolution) and Step 1 (Docker infrastructure with jv-api, jv-brain scaffold, jv-chromadb, and native Ollama). The implementation covers Steps 2-12 from the walkthrough document, divided into Phase A (backend RAG pipeline) and Phase B (API proxy and mobile UI).

## Glossary

- **Brain_Service**: The jv-brain FastAPI service running on port 8001 that implements the RAG pipeline
- **Document_Loader**: Component that reads vault files and extracts text content
- **Text_Chunker**: Component that splits documents into 512-token chunks with 64-token overlap
- **Embedding_Pipeline**: Component that converts text chunks into 768-dimensional vectors using nomic-embed-text
- **Vector_Store**: ChromaDB instance that stores and queries document embeddings
- **Retriever**: Component that performs similarity search to find relevant chunks for a query
- **Context_Assembler**: Component that constructs prompts with retrieved context and source attribution
- **Ollama_Client**: HTTP client that interfaces with Ollama for LLM inference
- **API_Proxy**: Routes in jv-api that forward AI requests to Brain_Service with JWT authentication
- **Mobile_AI_Chat**: Flutter feature that provides chat interface for querying the AI
- **Vault**: The /JARVIS directory containing user's markdown files
- **Secrets_Folder**: The /JARVIS/Secrets directory that must never be indexed
- **Incremental_Indexer**: Component that tracks file changes and only re-indexes modified files
- **NDJSON**: Newline-delimited JSON format used for streaming responses
- **Chunk**: A 512-token segment of document text with metadata (source path, index, hash)
- **Embedding**: A 768-dimensional float vector representing semantic meaning of text
- **RAG**: Retrieval-Augmented Generation - combining vector search with LLM inference

## Requirements

### Requirement 1: Brain Service Data Models

**User Story:** As a developer, I want well-defined Pydantic models for AI requests and responses, so that the API contract is type-safe and validated.

#### Acceptance Criteria

1. THE Brain_Service SHALL define an AskRequest model with query string, attachments list, and optional AskOptions
2. THE AskOptions model SHALL include top_k integer defaulting to 5, filter_paths list, include_sources boolean, and stream boolean
3. THE Brain_Service SHALL define an AskResponse model with answer string, sources list, model string, and tokens_used integer
4. THE Source model SHALL include path string, chunk integer, and score float
5. THE Brain_Service SHALL define an IndexStatus model with total_files_indexed, total_chunks, last_index_run timestamp, pending_files, and index_health status
6. WHEN models are imported in Python shell, THE Brain_Service SHALL successfully instantiate and serialize all models without errors

### Requirement 2: Document Loading from Vault

**User Story:** As a system, I want to load and extract text from all supported file types in the vault, so that content can be indexed for AI queries.

#### Acceptance Criteria

1. THE Document_Loader SHALL support markdown, txt, pdf, json, csv, and docx file extensions
2. THE Document_Loader SHALL extract text from PDF files using PyMuPDF library
3. THE Document_Loader SHALL extract text from DOCX files using python-docx library
4. THE Document_Loader SHALL convert JSON files to pretty-printed text using json.dumps with indent=2
5. THE Document_Loader SHALL convert CSV files to markdown table format
6. THE Document_Loader SHALL read markdown and txt files as UTF-8 text
7. THE Document_Loader SHALL exclude files in Secrets, .git, __pycache__, node_modules, and .system folders using a named constant EXCLUDED_FOLDERS
8. THE Document_Loader SHALL skip files larger than 50 megabytes
9. THE Document_Loader SHALL return a generator of LoadedDocument dataclasses containing path, content, last_modified timestamp, and content_hash
10. THE LoadedDocument content_hash SHALL be computed using SHA-256 of raw file bytes
11. WHEN a debug endpoint counts loaded files, THE Document_Loader SHALL return accurate file count matching vault contents
12. WHEN a debug endpoint checks exclusions, THE Document_Loader SHALL confirm no files from Secrets folder are included

### Requirement 3: Text Chunking for Embeddings

**User Story:** As a system, I want to split documents into token-sized chunks, so that embeddings fit within model context limits and retrieval is granular.

#### Acceptance Criteria

1. THE Text_Chunker SHALL split documents into chunks of 512 tokens each
2. THE Text_Chunker SHALL create 64-token overlap between consecutive chunks
3. THE Text_Chunker SHALL use recursive character text splitter with split sequence: double-newline, newline, period-space, space
4. THE Text_Chunker SHALL use tiktoken library with cl100k_base encoding for token counting
5. THE Text_Chunker SHALL return Chunk dataclasses with chunk_id, source_path, chunk_index, total_chunks, content, and content_hash
6. THE Chunk chunk_id SHALL be deterministic using SHA-256 of concatenated source_path, pipe character, and chunk_index
7. THE Chunk content_hash SHALL be SHA-256 of chunk content text
8. WHEN a 2000-word document is chunked, THE Text_Chunker SHALL produce approximately 8 chunks
9. WHEN consecutive chunks are compared, THE Text_Chunker SHALL show overlapping text between last 64 tokens of chunk N and first 64 tokens of chunk N+1

### Requirement 4: Embedding Generation

**User Story:** As a system, I want to convert text chunks into vector embeddings, so that semantic similarity search is possible.

#### Acceptance Criteria

1. THE Embedding_Pipeline SHALL use nomic-embed-text model via Ollama API
2. THE Embedding_Pipeline SHALL send POST requests to /api/embed endpoint with model parameter set to nomic-embed-text
3. THE Embedding_Pipeline SHALL batch 32 chunks per embedding request
4. THE Embedding_Pipeline SHALL return 768-dimensional float vectors for each chunk
5. THE Embedding_Pipeline SHALL use httpx.AsyncClient with connection pooling for HTTP requests
6. THE Embedding_Pipeline SHALL retry failed requests with exponential backoff up to 3 attempts
7. WHEN Ollama is temporarily unavailable, THE Embedding_Pipeline SHALL wait and retry before failing
8. WHEN a single string is embedded, THE Embedding_Pipeline SHALL return a vector with exactly 768 float values
9. WHEN embedding latency is measured, THE Embedding_Pipeline SHALL complete single chunk embedding in less than 1 second

### Requirement 5: Vector Storage in ChromaDB

**User Story:** As a system, I want to store and query document embeddings in ChromaDB, so that relevant context can be retrieved for user queries.

#### Acceptance Criteria

1. THE Vector_Store SHALL use chromadb-client library version 0.6.3
2. THE Vector_Store SHALL connect to ChromaDB at host chromadb port 8000
3. THE Vector_Store SHALL use collection name jarvis_vault
4. THE Vector_Store SHALL store chunk IDs as deterministic identifiers
5. THE Vector_Store SHALL store chunk content as documents
6. THE Vector_Store SHALL store 768-dimensional vectors as embeddings
7. THE Vector_Store SHALL store metadata including source_path, chunk_index, content_hash, and last_modified
8. THE Vector_Store SHALL provide upsert_chunks operation for inserting or updating chunks with embeddings
9. THE Vector_Store SHALL provide delete_by_path operation for removing all chunks matching a source_path
10. THE Vector_Store SHALL provide query operation accepting embedding, top_k, and filter_paths parameters
11. THE Vector_Store SHALL provide get_all_metadata operation for retrieving all stored chunk metadata
12. THE Vector_Store SHALL provide count operation returning total number of stored chunks
13. WHEN 5 test chunks are upserted and queried, THE Vector_Store SHALL return correct chunk with similarity score greater than 0.5
14. WHEN ChromaDB container is restarted, THE Vector_Store SHALL retain all previously stored data from jarvis-chroma-data volume

### Requirement 6: Incremental File Indexing

**User Story:** As a system, I want to track file changes and only re-index modified files, so that indexing is efficient and doesn't waste resources.

#### Acceptance Criteria

1. THE Incremental_Indexer SHALL walk vault files using Document_Loader
2. THE Incremental_Indexer SHALL compute content hash for each discovered file
3. THE Incremental_Indexer SHALL compare file hashes against Vector_Store metadata
4. WHEN a file path is not in Vector_Store, THE Incremental_Indexer SHALL chunk, embed, and insert the new file
5. WHEN a file hash differs from Vector_Store metadata, THE Incremental_Indexer SHALL delete old chunks, then chunk, embed, and insert the modified file
6. WHEN a file exists in Vector_Store but not on disk, THE Incremental_Indexer SHALL delete all chunks for that file
7. WHEN a file hash matches Vector_Store metadata, THE Incremental_Indexer SHALL skip processing that file
8. THE Incremental_Indexer SHALL track statistics including files_indexed, chunks_created, and files_skipped
9. THE Brain_Service SHALL expose POST /brain/reindex endpoint that triggers full re-indexing
10. THE Brain_Service SHALL expose GET /brain/index-status endpoint returning IndexStatus model
11. THE IndexStatus response SHALL include total_files_indexed, total_chunks, last_index_run ISO 8601 timestamp, pending_files, and index_health status
12. WHEN Brain_Service starts, THE Incremental_Indexer SHALL run indexing in background asyncio.Task
13. WHEN indexing is running, THE Brain_Service SHALL accept queries immediately using existing indexed content
14. WHEN POST /brain/reindex is called, THE Incremental_Indexer SHALL log summary with counts of new, modified, deleted, and unchanged files

### Requirement 7: Context Retrieval

**User Story:** As a system, I want to find relevant document chunks for a user query, so that the LLM has appropriate context to generate answers.

#### Acceptance Criteria

1. THE Retriever SHALL embed user query using Embedding_Pipeline with nomic-embed-text model
2. THE Retriever SHALL perform cosine similarity search in Vector_Store
3. THE Retriever SHALL retrieve top-K chunks where K defaults to 5
4. THE Retriever SHALL apply optional path-prefix filter when filter_paths parameter is provided
5. THE Retriever SHALL deduplicate results by collapsing multiple chunks from same source document
6. THE Retriever SHALL discard results with similarity score less than 0.3
7. WHEN query "project deadlines" is submitted, THE Retriever SHALL return chunks from relevant files with scores greater than 0.3
8. WHEN multiple chunks from same file match, THE Retriever SHALL include only highest-scoring chunk from that file

### Requirement 8: Prompt Assembly with Context

**User Story:** As a system, I want to construct prompts with retrieved context and source attribution, so that the LLM generates accurate answers with transparency.

#### Acceptance Criteria

1. THE Context_Assembler SHALL use prompt template identifying system as JARVIS personal AI assistant
2. THE Context_Assembler SHALL include instruction to answer using only provided context
3. THE Context_Assembler SHALL include instruction to state clearly when context is insufficient
4. THE Context_Assembler SHALL format context section with source attribution showing file path for each chunk
5. THE Context_Assembler SHALL enforce maximum context budget of 2048 tokens
6. THE Context_Assembler SHALL add chunks in score-descending order until token budget is exhausted
7. WHEN attachments parameter contains file paths, THE Context_Assembler SHALL read those files directly and include in context
8. WHEN attachments parameter is empty, THE Context_Assembler SHALL use only retrieved chunks for context
9. WHEN both attachments and retrieved chunks are available, THE Context_Assembler SHALL include attachments first then fill remaining budget with retrieved chunks
10. THE Context_Assembler SHALL preserve source path attribution for all context chunks

### Requirement 9: LLM Inference via Ollama

**User Story:** As a system, I want to send prompts to Ollama and receive streaming responses, so that users get real-time feedback during answer generation.

#### Acceptance Criteria

1. THE Ollama_Client SHALL send POST requests to /api/generate endpoint at Ollama URL
2. THE Ollama_Client SHALL use model parameter set to llama3
3. THE Ollama_Client SHALL set stream parameter to true for streaming responses
4. THE Ollama_Client SHALL set temperature parameter to 0.3 for factual responses
5. THE Ollama_Client SHALL set num_predict parameter to 1024 for maximum token limit
6. THE Ollama_Client SHALL parse newline-delimited JSON response format
7. THE Ollama_Client SHALL yield response tokens as AsyncGenerator
8. THE Ollama_Client SHALL extract eval_count from final response object when done is true
9. WHEN prompt "Say hello in one sentence" is sent, THE Ollama_Client SHALL return streaming response containing greeting in less than 15 seconds

### Requirement 10: End-to-End Ask Endpoint

**User Story:** As a user, I want to submit natural language queries and receive AI-generated answers with source citations, so that I can find information in my vault conversationally.

#### Acceptance Criteria

1. THE Brain_Service SHALL expose POST /brain/ask endpoint accepting AskRequest model
2. WHEN query is received, THE Brain_Service SHALL embed query using Embedding_Pipeline
3. WHEN query is embedded, THE Brain_Service SHALL retrieve relevant chunks using Retriever
4. WHEN attachments are specified, THE Brain_Service SHALL load attachment files using Document_Loader
5. WHEN context is gathered, THE Brain_Service SHALL assemble prompt using Context_Assembler
6. WHEN prompt is ready, THE Brain_Service SHALL send to Ollama using Ollama_Client
7. THE Brain_Service SHALL stream response tokens to caller using FastAPI StreamingResponse
8. WHEN LLM completes generation, THE Brain_Service SHALL return AskResponse with answer, sources list, model name, and tokens_used count
9. WHEN curl command with query is sent to /brain/ask, THE Brain_Service SHALL return streaming NDJSON response with answer and sources

### Requirement 11: API Proxy for AI Endpoints

**User Story:** As a mobile app, I want to access AI functionality through the main API with authentication, so that all communication goes through a single secure gateway.

#### Acceptance Criteria

1. THE API_Proxy SHALL create server/app/routers/ask.py router module
2. THE API_Proxy SHALL expose POST /ask endpoint that proxies to POST http://brain:8001/brain/ask
3. THE API_Proxy SHALL expose GET /ask/status endpoint that checks Brain_Service, Ollama, and ChromaDB health
4. THE API_Proxy SHALL expose GET /ask/index-status endpoint that proxies to GET http://brain:8001/brain/index-status
5. THE API_Proxy SHALL require JWT authentication for all /ask endpoints
6. THE API_Proxy SHALL use httpx.AsyncClient.stream for proxying streaming responses
7. THE API_Proxy SHALL forward response chunks via FastAPI StreamingResponse
8. THE API_Proxy SHALL add brain_url setting to server/app/config.py with default http://brain:8001
9. THE API_Proxy SHALL register ask router in server/app/main.py
10. THE API_Proxy SHALL not modify existing /files or /sync routes
11. WHEN curl with valid JWT token requests /ask/status, THE API_Proxy SHALL return JSON with ai_available boolean
12. WHEN curl with valid JWT token posts query to /ask, THE API_Proxy SHALL stream response from Brain_Service

### Requirement 12: Mobile AI Chat Interface

**User Story:** As a mobile user, I want a chat interface to query my vault using natural language, so that I can find information conversationally on my device.

#### Acceptance Criteria

1. THE Mobile_AI_Chat SHALL create mobile/lib/features/ai_chat directory structure
2. THE Mobile_AI_Chat SHALL create data/ai_repository.dart with Dio calls to POST /ask, GET /ask/status, and GET /ask/index-status
3. THE Mobile_AI_Chat SHALL create data/chat_history_table.dart with Drift table definition
4. THE Mobile_AI_Chat SHALL create presentation/ai_chat_provider.dart with Riverpod providers for chat state
5. THE Mobile_AI_Chat SHALL create presentation/ai_chat_screen.dart with message list, input field, and streaming display
6. THE Mobile_AI_Chat SHALL create presentation/widgets/chat_bubble.dart for rendering user and assistant messages
7. THE Mobile_AI_Chat SHALL create presentation/widgets/attachment_picker.dart for selecting vault files as context
8. THE Mobile_AI_Chat SHALL bump Drift schema from version 5 to version 6
9. THE chat_history table SHALL include id primary key, query text, response text, attachments text, and timestamp text columns
10. THE Mobile_AI_Chat SHALL not modify MutationQueue table
11. THE Mobile_AI_Chat SHALL not modify FileCacheEntries table
12. THE ai_repository SHALL use Dio with ResponseType.stream for streaming responses
13. THE ai_repository SHALL parse newline-delimited JSON response format
14. WHEN server is unreachable, THE Mobile_AI_Chat SHALL display "AI is offline" state
15. WHEN user submits query, THE Mobile_AI_Chat SHALL show streaming response tokens in real-time
16. WHEN response includes sources, THE Mobile_AI_Chat SHALL display source file paths as clickable links
17. THE Mobile_AI_Chat SHALL store chat history locally in SQLite
18. THE Mobile_AI_Chat SHALL not sync chat history to server

### Requirement 13: Environment Configuration

**User Story:** As a developer, I want all AI-related configuration in environment variables, so that the system is configurable without code changes.

#### Acceptance Criteria

1. THE Brain_Service SHALL read JARVIS_OLLAMA_URL from environment with default http://host.docker.internal:11434
2. THE Brain_Service SHALL read JARVIS_VECTORDB_URL from environment with default http://chromadb:8000
3. THE Brain_Service SHALL read JARVIS_BRAIN_URL from environment with default http://brain:8001
4. THE Brain_Service SHALL read JARVIS_EMBEDDING_MODEL from environment with default nomic-embed-text
5. THE Brain_Service SHALL read JARVIS_LLM_MODEL from environment with default llama3
6. THE Brain_Service SHALL update .env.template with all AI-related variables and descriptions

### Requirement 14: Dependency Management

**User Story:** As a developer, I want all required Python packages documented in requirements files, so that the environment can be reproduced reliably.

#### Acceptance Criteria

1. THE Brain_Service SHALL add PyMuPDF version 1.25.3 to brain/requirements.txt
2. THE Brain_Service SHALL add python-docx version 1.1.2 to brain/requirements.txt
3. THE Brain_Service SHALL add tiktoken version 0.9.0 to brain/requirements.txt
4. THE Brain_Service SHALL add chromadb-client version 0.6.3 to brain/requirements.txt

### Requirement 15: Phase 2 Isolation

**User Story:** As a system maintainer, I want Phase 3 to never modify Phase 2 files, so that the stable sync engine remains untouched.

#### Acceptance Criteria

1. THE Phase_3_Implementation SHALL not modify mobile/lib/features/sync/data/sync_repository.dart
2. THE Phase_3_Implementation SHALL not modify mobile/lib/core/storage/app_database.dart MutationQueue table definition
3. THE Phase_3_Implementation SHALL not modify mobile/lib/core/storage/app_database.dart FileCacheEntries table definition
4. THE Phase_3_Implementation SHALL not modify mobile/lib/features/sync/presentation/conflict_detail_screen.dart
5. THE Phase_3_Implementation SHALL not modify mobile/lib/features/sync/presentation/conflict_list_screen.dart
6. THE Phase_3_Implementation SHALL not modify mobile/lib/features/sync/presentation/conflict_provider.dart
7. THE Phase_3_Implementation SHALL not modify server/app/services/sync.py
8. THE Phase_3_Implementation SHALL not modify server/app/routers/sync.py
9. THE Phase_3_Implementation SHALL not modify server/app/routers/files.py existing routes

### Requirement 16: Security Constraints

**User Story:** As a user, I want my encrypted secrets never indexed and all AI processing local, so that my private data remains secure.

#### Acceptance Criteria

1. THE Document_Loader SHALL use named constant EXCLUDED_FOLDERS containing "Secrets" string
2. THE Document_Loader SHALL never read files from /JARVIS/Secrets directory
3. THE Document_Loader SHALL never pass Secrets folder content to Embedding_Pipeline
4. THE Vector_Store SHALL never contain chunks with source_path starting with "Secrets/"
5. THE Brain_Service SHALL not make HTTP requests to external APIs
6. THE Ollama_Client SHALL only connect to local Ollama instance
7. THE Embedding_Pipeline SHALL only connect to local Ollama instance

### Requirement 17: Nomic Embed Text Model Installation

**User Story:** As a system, I want the nomic-embed-text model installed before embedding operations, so that the pipeline can generate embeddings.

#### Acceptance Criteria

1. WHEN Step 5 implementation begins, THE nomic-embed-text model SHALL be pulled using ollama pull command
2. WHEN ollama list command is executed, THE nomic-embed-text model SHALL appear in installed models list
3. THE nomic-embed-text model SHALL be approximately 274 megabytes in size

### Requirement 18: Network Isolation

**User Story:** As a system administrator, I want Brain_Service and ChromaDB accessible only within Docker network, so that attack surface is minimized.

#### Acceptance Criteria

1. THE docker-compose.yml SHALL not expose Brain_Service port 8001 to host
2. THE docker-compose.yml SHALL not expose ChromaDB port 8000 to host
3. THE Brain_Service SHALL be accessible at http://brain:8001 from jv-api container
4. THE ChromaDB SHALL be accessible at http://chromadb:8000 from jv-brain container

### Requirement 19: Ollama Host Binding

**User Story:** As a system, I want Ollama bound to all interfaces, so that Docker containers can reach it from host.docker.internal.

#### Acceptance Criteria

1. THE Ollama service SHALL bind to 0.0.0.0:11434
2. WHEN netstat command checks port 11434, THE output SHALL show 0.0.0.0:11434 binding
3. WHEN Brain_Service sends request to http://host.docker.internal:11434, THE Ollama service SHALL respond successfully

### Requirement 20: Testing Requirements

**User Story:** As a developer, I want explicit test procedures for each component, so that I can verify correctness before proceeding.

#### Acceptance Criteria

1. WHEN Step 2 is complete, THE developer SHALL import models in Python shell and verify serialization works
2. WHEN Step 3 is complete, THE developer SHALL call debug endpoint and verify file count matches vault and Secrets folder is excluded
3. WHEN Step 4 is complete, THE developer SHALL chunk 2000-word file and verify approximately 8 chunks with overlap
4. WHEN Step 5 is complete, THE developer SHALL embed single string and verify 768 dimensions
5. WHEN Step 6 is complete, THE developer SHALL upsert 5 chunks, query, restart ChromaDB, and verify persistence
6. WHEN Step 7 is complete, THE developer SHALL POST to /brain/reindex and verify index-status shows counts
7. WHEN Step 8 is complete, THE developer SHALL query "project deadlines" and verify relevant chunks returned
8. WHEN Step 9 is complete, THE developer SHALL send hardcoded prompt and verify streaming response
9. WHEN Step 10 is complete, THE developer SHALL curl /brain/ask with query and verify streaming NDJSON response
10. WHEN Step 11 is complete, THE developer SHALL curl /ask/status with JWT and verify ai_available response
11. WHEN Step 12 is complete, THE developer SHALL manually test Flutter app chat interface and verify offline state renders

### Requirement 21: Parser and Pretty Printer for JSON

**User Story:** As a system, I want to parse JSON files and format them as readable text, so that JSON content can be indexed and retrieved.

#### Acceptance Criteria

1. THE Document_Loader SHALL parse JSON files using json.loads
2. THE Document_Loader SHALL format parsed JSON using json.dumps with indent parameter set to 2
3. WHEN invalid JSON file is encountered, THE Document_Loader SHALL log error and skip that file
4. FOR ALL valid JSON files, THE Document_Loader SHALL produce formatted text that can be parsed back to equivalent structure

### Requirement 22: Parser and Pretty Printer for CSV

**User Story:** As a system, I want to parse CSV files and format them as markdown tables, so that tabular data can be indexed and retrieved.

#### Acceptance Criteria

1. THE Document_Loader SHALL parse CSV files using csv.reader
2. THE Document_Loader SHALL format parsed CSV as markdown table with header row and separator row
3. WHEN invalid CSV file is encountered, THE Document_Loader SHALL log error and skip that file
4. FOR ALL valid CSV files, THE Document_Loader SHALL produce markdown table that preserves row and column structure
