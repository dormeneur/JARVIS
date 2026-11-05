"""jv-brain — JARVIS AI/RAG service.

Minimal scaffold: GET /brain/status healthcheck endpoint.
Pipeline components added in subsequent steps.
"""

import asyncio
import logging
from pathlib import Path

import httpx
from contextlib import asynccontextmanager
from fastapi import FastAPI, Request

from app.config import settings
from app.routers import ask, generate, chat, agent, forms, proposals, digest
from app.services.history_db import init_db
from app.services.document_loader import DocumentLoader
from app.services.text_chunker import TextChunker
from app.services.embedding_pipeline import EmbeddingPipeline
from app.services.vector_store import VectorStore
from app.services.keyword_index import KeywordIndex
from app.services.sqlite_vector_store import SqliteVectorStore, migrate_from_chroma
from app.services.incremental_indexer import IncrementalIndexer
from app.services.fs_tree import refresh_fs_tree
from app.services.scheduler import scheduler_loop
from app.services.tools import prune_trash

logging.basicConfig(level=settings.log_level)
logger = logging.getLogger("jv-brain")

async def build_vector_store():
    """The configured vector store. For sqlite, an empty store is filled once from ChromaDB."""
    if settings.vector_backend != "sqlite":
        return VectorStore(settings.vectordb_url)
    store = SqliteVectorStore(Path(settings.vault_path) / ".system" / "brain" / "vectors.db")
    if await store.count() == 0:
        try:
            copied = await migrate_from_chroma(VectorStore(settings.vectordb_url), store)
            logger.info(f"Vector store: copied {copied} chunks from ChromaDB into SQLite (no re-embedding)")
        except Exception as e:
            logger.warning(f"No ChromaDB data to copy ({e}); the indexer will build the SQLite store from scratch")
    return store


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Initialize services
    app.state.document_loader = DocumentLoader(settings.vault_path)
    app.state.text_chunker = TextChunker()
    app.state.embedding_pipeline = EmbeddingPipeline(settings.ollama_url)
    app.state.vector_store = await build_vector_store()
    # BM25 index next to the vector store (None if this SQLite has no FTS5: search stays vector-only).
    try:
        app.state.keyword_index = KeywordIndex(Path(settings.vault_path) / ".system" / "brain" / "fts.db")
    except Exception as e:
        logger.warning(f"Keyword index unavailable, using vector search only: {e}")
        app.state.keyword_index = None
    
    app.state.indexer = IncrementalIndexer(
        app.state.document_loader,
        app.state.text_chunker,
        app.state.embedding_pipeline,
        app.state.vector_store,
        app.state.keyword_index,
    )
    
    # Start background indexing
    app.state.indexer.start_background_indexing()

    # Initialize history database
    init_db()

    # Drop trash older than 30 days (deleted/overwritten files the agent kept for undo).
    try:
        removed = prune_trash()
        if removed:
            logger.info(f"Pruned {removed} old trash batches")
    except Exception as e:
        logger.warning(f"Trash prune failed: {e}")

    # Build initial filesystem tree snapshot for LLM context
    try:
        tree = refresh_fs_tree()
        logger.info(f"Built initial fs_tree snapshot with {len(tree)} paths")
    except Exception as e:
        logger.warning(f"Failed to build initial fs_tree: {e}")

    scheduler_task = asyncio.create_task(scheduler_loop(app))

    yield

    scheduler_task.cancel()
    
    # Shutdown
    await app.state.embedding_pipeline.close()

app = FastAPI(
    title="JARVIS Brain",
    description="Local AI retrieval and reasoning pipeline",
    version="0.1.0",
    lifespan=lifespan
)

# Register routers
app.include_router(ask.router)
app.include_router(generate.router)
app.include_router(chat.router)
app.include_router(agent.router)
app.include_router(forms.router)
app.include_router(proposals.router)
app.include_router(digest.router)


@app.get("/brain/status")
async def brain_status():
    """Health check — reports whether Ollama and ChromaDB are reachable."""
    ollama_ok = False
    chromadb_ok = False

    async with httpx.AsyncClient(timeout=5.0) as client:
        # Check Ollama
        try:
            r = await client.get(f"{settings.ollama_url}/api/tags")
            ollama_ok = r.status_code == 200
        except Exception:
            logger.warning("Ollama unreachable at %s", settings.ollama_url)

        # Check ChromaDB (not used by the sqlite backend)
        if settings.vector_backend == "sqlite":
            chromadb_ok = True
        else:
            try:
                r = await client.get(f"{settings.vectordb_url}/api/v2/heartbeat")
                chromadb_ok = r.status_code == 200
            except Exception:
                logger.warning("ChromaDB unreachable at %s", settings.vectordb_url)

    return {
        "status": "ok" if (ollama_ok and chromadb_ok) else "degraded",
        "ollama": "reachable" if ollama_ok else "unreachable",
        "chromadb": ("not used" if settings.vector_backend == "sqlite" else "reachable") if chromadb_ok else "unreachable",
        "indexing": getattr(app.state, "indexer", None).is_indexing if hasattr(app.state, "indexer") else False,
    }
