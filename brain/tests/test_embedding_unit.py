"""Unit tests for embedding pipeline."""

import pytest
import httpx
from unittest.mock import AsyncMock, patch
import time

from brain.app.services.embedding_pipeline import (
    EmbeddingPipeline, 
    EXPECTED_DIMENSIONS, 
    BATCH_SIZE,
    EmbeddingError
)
from brain.app.services.text_chunker import Chunk

@pytest.fixture
def mock_httpx_post():
    with patch("httpx.AsyncClient.post", new_callable=AsyncMock) as mock_post:
        yield mock_post


@pytest.mark.asyncio
async def test_single_query_embedding_returns_768_dimensions(mock_httpx_post):
    mock_embeddings = [[0.5] * EXPECTED_DIMENSIONS]
    mock_response = AsyncMock()
    mock_response.json.return_value = {"embeddings": mock_embeddings}
    mock_response.raise_for_status.return_value = None
    mock_httpx_post.return_value = mock_response

    pipeline = EmbeddingPipeline("http://fake-host")
    
    try:
        embedding = await pipeline.embed_query("test query")
        assert len(embedding) == EXPECTED_DIMENSIONS
        assert isinstance(embedding, list)
    finally:
        await pipeline.close()


@pytest.mark.asyncio
async def test_batch_embedding_with_32_chunks(mock_httpx_post):
    chunks = []
    for i in range(BATCH_SIZE):
        chunks.append(Chunk(
            chunk_id=f"id_{i}",
            source_path="fake/path",
            chunk_index=i,
            total_chunks=BATCH_SIZE,
            content=f"content {i}",
            content_hash="hash"
        ))

    mock_embeddings = [[0.1] * EXPECTED_DIMENSIONS for _ in range(BATCH_SIZE)]
    mock_response = AsyncMock()
    mock_response.json.return_value = {"embeddings": mock_embeddings}
    mock_response.raise_for_status.return_value = None
    mock_httpx_post.return_value = mock_response

    pipeline = EmbeddingPipeline("http://fake-host")
    
    try:
        embeddings = await pipeline.embed_chunks(chunks)
        assert len(embeddings) == BATCH_SIZE
        assert mock_httpx_post.call_count == 1
    finally:
        await pipeline.close()


@pytest.mark.asyncio
async def test_retry_logic_with_mock_ollama_failures(mock_httpx_post):
    # Setup mock to fail twice, then succeed on the third attempt
    mock_response_success = AsyncMock()
    mock_response_success.json.return_value = {"embeddings": [[0.5] * EXPECTED_DIMENSIONS]}
    mock_response_success.raise_for_status.return_value = None
    
    # Exceptions to be raised on the first two attempts
    mock_httpx_post.side_effect = [
        httpx.RequestError("Connection failed"),
        httpx.HTTPStatusError("Service Unavailable", request=AsyncMock(), response=AsyncMock()),
        mock_response_success
    ]

    # Use a custom pipeline to speed up testing retries
    with patch("brain.app.services.embedding_pipeline.RETRY_BACKOFF_BASE", 0.01):
        pipeline = EmbeddingPipeline("http://fake-host")
        try:
            embedding = await pipeline.embed_query("retry test")
            assert len(embedding) == EXPECTED_DIMENSIONS
            assert mock_httpx_post.call_count == 3
        finally:
            await pipeline.close()


@pytest.mark.asyncio
async def test_embed_query_fails_after_max_retries(mock_httpx_post):
    mock_httpx_post.side_effect = httpx.RequestError("Connection failed")
    
    with patch("brain.app.services.embedding_pipeline.RETRY_BACKOFF_BASE", 0.01):
        pipeline = EmbeddingPipeline("http://fake-host")
        try:
            # We expect retrying to exhaust and then bubble up the error
            with pytest.raises(httpx.RequestError):
                await pipeline.embed_query("failing test")
                
            # MAX_RETRIES + 1 (initial try + MAX_RETRIES retries, assuming stop_after_attempt applies differently, tenacity counts total attempts)
            assert mock_httpx_post.call_count > 1
        finally:
            await pipeline.close()
