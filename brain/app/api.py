"""jv-brain — JARVIS AI/RAG service.

Minimal scaffold: GET /brain/status healthcheck endpoint.
Pipeline components added in subsequent steps.
"""

import logging

import httpx
from contextlib import asynccontextmanager
from fastapi import FastAPI, Request

from app.config import settings
from app.routers import debug, ask, generate, chat
from app.services.history_db import init_db
from app.services.document_loader import DocumentLoader
from app.services.text_chunker import TextChunker
from app.services.embedding_pipeline import EmbeddingPipeline
from app.services.vector_store import VectorStore
from app.services.incremental_indexer import IncrementalIndexer
from app.services.fs_tree import refresh_fs_tree

logging.basicConfig(level=settings.log_level)
logger = logging.getLogger("jv-brain")

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Initialize services
    app.state.document_loader = DocumentLoader(settings.vault_path)
    app.state.text_chunker = TextChunker()
    app.state.embedding_pipeline = EmbeddingPipeline(settings.ollama_url)
    app.state.vector_store = VectorStore(settings.vectordb_url)
    
    app.state.indexer = IncrementalIndexer(
        app.state.document_loader,
        app.state.text_chunker,
        app.state.embedding_pipeline,
        app.state.vector_store
    )
    
    # Start background indexing
    app.state.indexer.start_background_indexing()

    # Initialize history database
    init_db()

    # Build initial filesystem tree snapshot for LLM context
    try:
        tree = refresh_fs_tree()
        logger.info(f"Built initial fs_tree snapshot with {len(tree)} paths")
    except Exception as e:
        logger.warning(f"Failed to build initial fs_tree: {e}")

    yield
    
    # Shutdown
    await app.state.embedding_pipeline.close()

app = FastAPI(
    title="JARVIS Brain",
    description="Local AI retrieval and reasoning pipeline",
    version="0.1.0",
    lifespan=lifespan
)

# Register routers
app.include_router(debug.router)
app.include_router(ask.router)
app.include_router(generate.router)
app.include_router(chat.router)


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

        # Check ChromaDB
        try:
            r = await client.get(f"{settings.vectordb_url}/api/v2/heartbeat")
            chromadb_ok = r.status_code == 200
        except Exception:
            logger.warning("ChromaDB unreachable at %s", settings.vectordb_url)

    return {
        "status": "ok" if (ollama_ok and chromadb_ok) else "degraded",
        "ollama": "reachable" if ollama_ok else "unreachable",
        "chromadb": "reachable" if chromadb_ok else "unreachable",
        "indexing": getattr(app.state, "indexer", None).is_indexing if hasattr(app.state, "indexer") else False,
    }
