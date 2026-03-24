"""Embedding pipeline service using Ollama."""

import logging
from typing import List

import httpx
from tenacity import retry, stop_after_attempt, wait_exponential, retry_if_exception_type

from app.services.text_chunker import Chunk

logger = logging.getLogger(__name__)

# Constants
EMBEDDING_MODEL = "nomic-embed-text"
BATCH_SIZE = 32
MAX_RETRIES = 3
RETRY_BACKOFF_BASE = 2
OLLAMA_EMBED_API_PATH = "/api/embed"
EXPECTED_DIMENSIONS = 768

class EmbeddingError(Exception):
    """Exception raised for errors in the embedding process."""
    pass

class EmbeddingPipeline:
    """Service to generate embeddings using Ollama."""
    
    def __init__(self, ollama_url: str):
        """Initialize pipeline with Ollama URL."""
        self.ollama_url = ollama_url.rstrip("/")
        # We reuse the client for connection pooling
        self.client = httpx.AsyncClient(timeout=30.0)

    async def close(self):
        """Close the HTTP client."""
        await self.client.aclose()

    @retry(
        stop=stop_after_attempt(MAX_RETRIES),
        wait=wait_exponential(multiplier=RETRY_BACKOFF_BASE, min=1, max=10),
        retry=retry_if_exception_type((httpx.RequestError, httpx.HTTPStatusError)),
        reraise=True
    )
    async def embed_batch(self, texts: List[str]) -> List[List[float]]:
        """Send a batch of texts to Ollama /api/embed endpoint.
        
        Args:
            texts: List of strings to embed.
            
        Returns:
            List of 768-dimensional float vectors.
        """
        if not texts:
            return []
            
        try:
            response = await self.client.post(
                f"{self.ollama_url}{OLLAMA_EMBED_API_PATH}",
                json={
                    "model": EMBEDDING_MODEL,
                    "input": texts
                }
            )
            response.raise_for_status()
            data = response.json()
            
            embeddings = data.get("embeddings", [])
            if not embeddings:
                raise EmbeddingError("Ollama response did not contain 'embeddings'")
                
            if len(embeddings) != len(texts):
                raise EmbeddingError(f"Expected {len(texts)} embeddings, got {len(embeddings)}")
                
            # Verify dimensions
            for i, emb in enumerate(embeddings):
                if len(emb) != EXPECTED_DIMENSIONS:
                    raise EmbeddingError(f"Expected {EXPECTED_DIMENSIONS} dimensions, got {len(emb)}")
                    
            return embeddings
            
        except (httpx.RequestError, httpx.HTTPStatusError) as e:
            logger.error("Failed to generate embeddings: %s", str(e))
            raise

    async def embed_chunks(self, chunks: List[Chunk]) -> List[List[float]]:
        """Generate embeddings for a list of Chunk objects in batches.
        
        Args:
            chunks: List of Chunk objects
            
        Returns:
            List of 768-dimensional float vectors, matching the order of chunks.
        """
        all_embeddings = []
        
        for i in range(0, len(chunks), BATCH_SIZE):
            batch_chunks = chunks[i:i + BATCH_SIZE]
            batch_texts = [chunk.content for chunk in batch_chunks]
            batch_embeddings = await self.embed_batch(batch_texts)
            all_embeddings.extend(batch_embeddings)
            
        return all_embeddings

    async def embed_query(self, query: str) -> List[float]:
        """Generate an embedding for a single query string.
        
        Args:
            query: The search query string.
            
        Returns:
            A single 768-dimensional float vector.
        """
        embeddings = await self.embed_batch([query])
        return embeddings[0]
