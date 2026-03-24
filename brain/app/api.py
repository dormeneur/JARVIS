"""jv-brain — JARVIS AI/RAG service.

Minimal scaffold: GET /brain/status healthcheck endpoint.
Pipeline components added in subsequent steps.
"""

import logging

import httpx
from fastapi import FastAPI

from app.config import settings

logging.basicConfig(level=settings.log_level)
logger = logging.getLogger("jv-brain")

app = FastAPI(
    title="JARVIS Brain",
    description="Local AI retrieval and reasoning pipeline",
    version="0.1.0",
)


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
        "indexing": False,
    }
