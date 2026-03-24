"""Property-based tests for embedding pipeline.

Feature: phase-3-ai-integration
Properties tested:
- Property 9: Embedding Dimensions
"""

import pytest
from hypothesis import given, settings, strategies as st

from brain.app.services.embedding_pipeline import EmbeddingPipeline, EXPECTED_DIMENSIONS
import asyncio
from unittest.mock import AsyncMock, patch

@pytest.fixture
def mock_httpx_post():
    """Mock httpx.AsyncClient.post to avoid real API calls in property tests."""
    with patch("httpx.AsyncClient.post", new_callable=AsyncMock) as mock_post:
        yield mock_post

@settings(max_examples=50)
@given(
    texts=st.lists(st.text(min_size=1, max_size=100), min_size=1, max_size=10),
    url=st.urls()
)
@pytest.mark.asyncio
async def test_property_9_embedding_dimensions_structure(mock_httpx_post, texts, url):
    """Property 9: Embedding Dimensions Structure
    
    **Validates: Requirement 4.4**
    
    Verify all embeddings have exactly 768 dimensions.
    
    Tag: Feature: phase-3-ai-integration, Property 9: Embedding Dimensions
    """
    # Create mock response matching the length of input list
    mock_response = AsyncMock()
    # Return exactly EXPECTED_DIMENSIONS float values for each input text
    mock_embeddings = [[0.1] * EXPECTED_DIMENSIONS for _ in texts]
    mock_response.json.return_value = {"embeddings": mock_embeddings}
    mock_response.raise_for_status.return_value = None
    mock_httpx_post.return_value = mock_response

    # Verify our logic processes this correctly
    pipeline = EmbeddingPipeline(ollama_url=url)
    
    try:
        embeddings = await pipeline.embed_batch(texts)
        
        assert len(embeddings) == len(texts), "Must return one embedding per input text"
        for i, emb in enumerate(embeddings):
            assert len(emb) == EXPECTED_DIMENSIONS, f"Embedding {i} has incorrect dimension, expected {EXPECTED_DIMENSIONS}"
            assert all(isinstance(x, float) for x in emb), f"Embedding {i} contains non-float values"
    finally:
        await pipeline.close()
