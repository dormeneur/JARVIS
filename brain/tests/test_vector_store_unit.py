"""Unit tests for vector storage."""

import pytest
from unittest.mock import MagicMock, patch

from brain.app.services.vector_store import VectorStore, EXPECTED_DIMENSIONS
from brain.app.services.text_chunker import Chunk

@pytest.fixture
def mock_chroma_client():
    with patch("chromadb.HttpClient") as mock_client_class:
        mock_client = MagicMock()
        mock_collection = MagicMock()
        mock_client.get_or_create_collection.return_value = mock_collection
        mock_client_class.return_value = mock_client
        yield mock_collection

@pytest.mark.asyncio
async def test_upsert_empty_list(mock_chroma_client):
    store = VectorStore("http://fake:8000")
    await store.upsert_chunks([], [], "timestamp")
    assert not mock_chroma_client.upsert.called

@pytest.mark.asyncio
async def test_upsert_mismatched_lengths(mock_chroma_client):
    store = VectorStore("http://fake:8000")
    chunk = Chunk("id", "path", 0, 1, "content", "hash")
    
    with pytest.raises(ValueError, match="Mismatched chunks"):
        await store.upsert_chunks([chunk], [], "timestamp")

@pytest.mark.asyncio
async def test_query_filtering(mock_chroma_client):
    store = VectorStore("http://fake:8000")
    mock_chroma_client.query.return_value = {"ids": [["id1"]], "distances": [[0.1]]}
    
    emb = [0.1] * EXPECTED_DIMENSIONS
    
    # 1 filter
    await store.query(emb, top_k=5, filter_paths=["file1.md"])
    kwargs = mock_chroma_client.query.call_args[1]
    assert kwargs["where"] == {"source_path": "file1.md"}
    
    # >1 filters
    await store.query(emb, top_k=5, filter_paths=["file1.md", "file2.md"])
    kwargs = mock_chroma_client.query.call_args[1]
    assert kwargs["where"] == {"source_path": {"$in": ["file1.md", "file2.md"]}}

@pytest.mark.asyncio
async def test_get_all_metadata(mock_chroma_client):
    store = VectorStore("http://fake:8000")
    mock_chroma_client.get.return_value = {"metadatas": [{"source_path": "path"}]}
    
    res = await store.get_all_metadata()
    assert len(res) == 1
    assert res[0]["source_path"] == "path"
    kwargs = mock_chroma_client.get.call_args[1]
    assert kwargs["include"] == ["metadatas"]
