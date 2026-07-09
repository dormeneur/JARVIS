"""Property-based tests for vector store.

Feature: phase-3-ai-integration
Properties tested:
- Property 2: Vector Store Round-Trip
- Property 10: Vector Store Deletion
- Property 11: Vector Store Count Accuracy
"""

import pytest
from hypothesis import given, settings, strategies as st, HealthCheck
from unittest.mock import MagicMock, patch

from brain.app.services.vector_store import VectorStore, EMBEDDING_DIMENSION
from brain.app.services.text_chunker import Chunk


def _make_mock_client():
    mock_client = MagicMock()
    mock_collection = MagicMock()
    mock_client.get_or_create_collection.return_value = mock_collection
    
    mock_client_class = patch("brain.app.services.vector_store.chromadb.HttpClient").start()
    mock_client_class.return_value = mock_client
    return mock_collection, mock_client_class


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

@settings(max_examples=30)
@given(chunks=chunk_strategy())
@pytest.mark.asyncio
async def test_property_2_vector_store_round_trip(chunks):
    """Property 2: Vector Store Round-Trip
    
    **Validates: Requirements 5.4, 5.5, 5.6, 5.7**
    
    Verify upsert calls collection.upsert with correct structures.
    
    Tag: Feature: phase-3-ai-integration, Property 2: Vector Store Round-Trip
    """
    mock_collection, mock_client_class = _make_mock_client()
    try:
        embeddings = [[0.1] * EMBEDDING_DIMENSION for _ in chunks]
        store = VectorStore("http://fake-host:8000")
        
        await store.upsert_chunks(chunks, embeddings, "2026-03-24T00:00:00Z")
        
        # Verify upsert was called
        assert mock_collection.upsert.called
        kwargs = mock_collection.upsert.call_args[1]
        
        assert len(kwargs['ids']) == len(chunks)
        assert len(kwargs['embeddings']) == len(chunks)
        assert len(kwargs['documents']) == len(chunks)
        assert len(kwargs['metadatas']) == len(chunks)
        
        for i, meta in enumerate(kwargs['metadatas']):
            assert meta['source_path'] == chunks[i].source_path
            assert meta['chunk_index'] == chunks[i].chunk_index
    finally:
        mock_client_class.stop()

@settings(max_examples=20)
@given(path=st.text(min_size=1, max_size=20))
@pytest.mark.asyncio
async def test_property_10_vector_store_deletion(path):
    """Property 10: Vector Store Deletion
    
    **Validates: Requirement 5.9**
    
    Verify delete_by_path calls delete with proper path filter.
    
    Tag: Feature: phase-3-ai-integration, Property 10: Vector Store Deletion
    """
    mock_collection, mock_client_class = _make_mock_client()
    try:
        store = VectorStore("http://fake-host:8000")
        
        await store.delete_by_path(path)
        
        assert mock_collection.delete.called
        kwargs = mock_collection.delete.call_args[1]
        assert kwargs['where'] == {"source_path": path}
    finally:
        mock_client_class.stop()

@settings(max_examples=20)
@given(count_val=st.integers(0, 1000))
@pytest.mark.asyncio
async def test_property_11_vector_store_count(count_val):
    """Property 11: Vector Store Count Accuracy
    
    **Validates: Requirement 5.12**
    
    Verify count() returns value from collection.count().
    
    Tag: Feature: phase-3-ai-integration, Property 11: Vector Store Count Accuracy
    """
    mock_collection, mock_client_class = _make_mock_client()
    try:
        mock_collection.count.return_value = count_val
        store = VectorStore("http://fake-host:8000")
        
        c = await store.count()
        assert c == count_val
        assert mock_collection.count.called
    finally:
        mock_client_class.stop()
