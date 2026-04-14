"""Unit tests for embedding pipeline."""

import pytest
import httpx
from unittest.mock import AsyncMock, MagicMock, patch

from brain.app.services.embedding_pipeline import (
    EmbeddingPipeline,
    EXPECTED_DIMENSIONS,
    BATCH_SIZE,
    EmbeddingError
)
from brain.app.services.text_chunker import Chunk


def _make_mock_client(mock_post):
    """Create a mock httpx.AsyncClient with the given post mock."""
    mock_client = MagicMock()
    mock_client.post = mock_post
    mock_client.aclose = AsyncMock()
    return mock_client


@pytest.mark.asyncio
async def test_single_query_embedding_returns_768_dimensions():
    mock_embeddings = [[0.5] * EXPECTED_DIMENSIONS]
    mock_response = MagicMock()
    mock_response.json.return_value = {"embeddings": mock_embeddings}
    mock_response.raise_for_status.return_value = None

    mock_post = AsyncMock(return_value=mock_response)

    with patch("brain.app.services.embedding_pipeline.httpx.AsyncClient",
               return_value=_make_mock_client(mock_post)):
        pipeline = EmbeddingPipeline("http://fake-host")
        try:
            embedding = await pipeline.embed_query("test query")
            assert len(embedding) == EXPECTED_DIMENSIONS
            assert isinstance(embedding, list)
        finally:
            await pipeline.close()


@pytest.mark.asyncio
async def test_batch_embedding_with_32_chunks():
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
    mock_response = MagicMock()
    mock_response.json.return_value = {"embeddings": mock_embeddings}
    mock_response.raise_for_status.return_value = None

    mock_post = AsyncMock(return_value=mock_response)

    with patch("brain.app.services.embedding_pipeline.httpx.AsyncClient",
               return_value=_make_mock_client(mock_post)):
        pipeline = EmbeddingPipeline("http://fake-host")
        try:
            embeddings = await pipeline.embed_chunks(chunks)
            assert len(embeddings) == BATCH_SIZE
            assert mock_post.call_count == 1
        finally:
            await pipeline.close()


@pytest.mark.asyncio
async def test_retry_logic_with_mock_ollama_failures():
    # Setup mock to fail twice, then succeed on the third attempt
    mock_response_success = MagicMock()
    mock_response_success.json.return_value = {"embeddings": [[0.5] * EXPECTED_DIMENSIONS]}
    mock_response_success.raise_for_status.return_value = None

    # Exceptions to be raised on the first two attempts
    mock_post = AsyncMock(side_effect=[
        httpx.RequestError("Connection failed"),
        httpx.HTTPStatusError("Service Unavailable", request=MagicMock(), response=MagicMock()),
        mock_response_success
    ])

    with patch("brain.app.services.embedding_pipeline.httpx.AsyncClient",
               return_value=_make_mock_client(mock_post)):
        pipeline = EmbeddingPipeline("http://fake-host")
        try:
            embedding = await pipeline.embed_query("retry test")
            assert len(embedding) == EXPECTED_DIMENSIONS
            assert mock_post.call_count == 3
        finally:
            await pipeline.close()


@pytest.mark.asyncio
async def test_embed_query_fails_after_max_retries():
    mock_post = AsyncMock(side_effect=httpx.RequestError("Connection failed"))

    with patch("brain.app.services.embedding_pipeline.httpx.AsyncClient",
               return_value=_make_mock_client(mock_post)):
        pipeline = EmbeddingPipeline("http://fake-host")
        try:
            with pytest.raises(httpx.RequestError):
                await pipeline.embed_query("failing test")

            assert mock_post.call_count > 1
        finally:
            await pipeline.close()
