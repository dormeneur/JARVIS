"""Property-based tests for vector store.

Feature: phase-3-ai-integration
Properties tested:
- Property 2: Vector Store Round-Trip
- Property 10: Vector Store Deletion
- Property 11: Vector Store Count Accuracy
"""

import pytest
from hypothesis import given, settings, strategies as st
from unittest.mock import MagicMock, patch

from brain.app.services.vector_store import VectorStore, EXPECTED_DIMENSIONS
from brain.app.services.text_chunker import Chunk


@pytest.fixture
def mock_chroma_client():
    """Mock the chromadb.HttpClient."""
    with patch("chromadb.HttpClient") as mock_client_class:
        mock_client = MagicMock()
        mock_collection = MagicMock()
        mock_client.get_or_create_collection.return_value = mock_collection
        mock_client_class.return_value = mock_client
        yield mock_collection


@st.composite
def chunk_strategy(draw, num_chunks=st.integers(1, 10)):
    n = draw(num_chunks)
    chunks = []
    for i in range(n):
        chunks.append(Chunk(
            chunk_id=draw(st.text(min_size=1, max_size=10)),
            source_path=draw(st.text(min_size=1, max_size=10)),
            chunk_index=i,
            total_chunks=n,
            content=draw(st.text(min_size=1, max_size=100)),
            content_hash=draw(st.text(min_size=10, max_size=10))
        ))
    return chunks

@settings(max_examples=50)
@given(chunks=chunk_strategy())
@pytest.mark.asyncio
async def test_property_2_vector_store_round_trip(mock_chroma_client, chunks):
    """Property 2: Vector Store Round-Trip
    
    **Validates: Requirements 5.4, 5.5, 5.6, 5.7**
    
    Verify upsert calls collection.upsert with correct structures.
    
    Tag: Feature: phase-3-ai-integration, Property 2: Vector Store Round-Trip
    """
    embeddings = [[0.1] * EXPECTED_DIMENSIONS for _ in chunks]
    store = VectorStore("http://fake-host:8000")
    
    await store.upsert_chunks(chunks, embeddings, "2026-03-24T00:00:00Z")
    
    # Verify upsert was called
    assert mock_chroma_client.upsert.called
    kwargs = mock_chroma_client.upsert.call_args[1]
    
    assert len(kwargs['ids']) == len(chunks)
    assert len(kwargs['embeddings']) == len(chunks)
    assert len(kwargs['documents']) == len(chunks)
    assert len(kwargs['metadatas']) == len(chunks)
    
    for i, meta in enumerate(kwargs['metadatas']):
        assert meta['source_path'] == chunks[i].source_path
        assert meta['chunk_index'] == chunks[i].chunk_index

@settings(max_examples=50)
@given(path=st.text(min_size=1, max_size=20))
@pytest.mark.asyncio
async def test_property_10_vector_store_deletion(mock_chroma_client, path):
    """Property 10: Vector Store Deletion
    
    **Validates: Requirement 5.9**
    
    Verify delete_by_path calls delete with proper path filter.
    
    Tag: Feature: phase-3-ai-integration, Property 10: Vector Store Deletion
    """
    store = VectorStore("http://fake-host:8000")
    
    await store.delete_by_path(path)
    
    assert mock_chroma_client.delete.called
    kwargs = mock_chroma_client.delete.call_args[1]
    assert kwargs['where'] == {"source_path": path}

@settings(max_examples=50)
@given(count_val=st.integers(0, 1000))
@pytest.mark.asyncio
async def test_property_11_vector_store_count(mock_chroma_client, count_val):
    """Property 11: Vector Store Count Accuracy
    
    **Validates: Requirement 5.12**
    
    Verify count() returns value from collection.count().
    
    Tag: Feature: phase-3-ai-integration, Property 11: Vector Store Count Accuracy
    """
    mock_chroma_client.count.return_value = count_val
    store = VectorStore("http://fake-host:8000")
    
    c = await store.count()
    assert c == count_val
    assert mock_chroma_client.count.called
