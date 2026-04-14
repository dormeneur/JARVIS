"""Property-based tests for embedding pipeline.

Feature: phase-3-ai-integration
Properties tested:
- Property 9: Embedding Dimensions
"""

import pytest
from hypothesis import given, settings, strategies as st

from brain.app.services.embedding_pipeline import EmbeddingPipeline, EXPECTED_DIMENSIONS
from unittest.mock import AsyncMock, MagicMock, patch


def _make_mock_client(mock_post):
    """Create a mock httpx.AsyncClient with the given post mock."""
    mock_client = MagicMock()
    mock_client.post = mock_post
    mock_client.aclose = AsyncMock()
    return mock_client


@settings(max_examples=50)
@given(
    texts=st.lists(st.text(min_size=1, max_size=100), min_size=1, max_size=10),
    url=st.just("http://fake-ollama:11434")
)
@pytest.mark.asyncio
async def test_property_9_embedding_dimensions_structure(texts, url):
    """Property 9: Embedding Dimensions Structure

    **Validates: Requirement 4.4**

    Verify all embeddings have exactly 768 dimensions.

    Tag: Feature: phase-3-ai-integration, Property 9: Embedding Dimensions
    """
    # Create mock response matching the length of input list
    mock_embeddings = [[0.1] * EXPECTED_DIMENSIONS for _ in texts]
    mock_response = MagicMock()
    mock_response.json.return_value = {"embeddings": mock_embeddings}
    mock_response.raise_for_status.return_value = None

    mock_post = AsyncMock(return_value=mock_response)

    with patch("brain.app.services.embedding_pipeline.httpx.AsyncClient",
               return_value=_make_mock_client(mock_post)):
        pipeline = EmbeddingPipeline(ollama_url=url)

        try:
            embeddings = await pipeline.embed_batch(texts)

            assert len(embeddings) == len(texts), "Must return one embedding per input text"
            for i, emb in enumerate(embeddings):
                assert len(emb) == EXPECTED_DIMENSIONS, f"Embedding {i} has incorrect dimension, expected {EXPECTED_DIMENSIONS}"
                assert all(isinstance(x, float) for x in emb), f"Embedding {i} contains non-float values"
        finally:
            await pipeline.close()
