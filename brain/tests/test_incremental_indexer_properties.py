"""Property-based tests for incremental indexer.

Feature: phase-3-ai-integration
Properties tested:
- Property 12: Incremental Indexing - New Files
- Property 13: Incremental Indexing - Modified Files
- Property 14: Incremental Indexing - Deleted Files
- Property 15: Incremental Indexing - Unchanged Files
- Property 16: Indexing Statistics Accuracy
- Property 39: IndexStatus Structure
"""

import pytest
from unittest.mock import MagicMock, AsyncMock, patch
from hypothesis import given, settings, strategies as st, HealthCheck
from datetime import datetime

from brain.app.services.incremental_indexer import IncrementalIndexer
from brain.app.services.document_loader import LoadedDocument
from brain.app.services.text_chunker import Chunk

def _make_indexer_mocks():
    loader = MagicMock()
    chunker = MagicMock()
    embedder = MagicMock()
    store = MagicMock()

    # Wire async methods
    embedder.ollama_url = "http://fake"
    embedder.embed_chunks = AsyncMock(return_value=[[0.1]*768])
    store.get_all_metadata = AsyncMock(return_value=[])
    store.upsert_chunks = AsyncMock()
    store.delete_by_path = AsyncMock()

    # Sync methods
    chunker.chunk_document.return_value = [Chunk("id1","path1",0,1,"content","hash")]

    return loader, chunker, embedder, store


@pytest.fixture
def patch_summary():
    """Patch OllamaClient.generate so indexer doesn't make real HTTP calls for summaries."""
    with patch("brain.app.services.incremental_indexer.OllamaClient") as mock_cls:
        mock_instance = MagicMock()
        mock_instance.generate = AsyncMock(return_value=("Summary.", 5))
        mock_cls.return_value = mock_instance
        # Also patch _update_global_memory to avoid filesystem writes
        with patch.object(IncrementalIndexer, "_update_global_memory", new_callable=AsyncMock):
            yield


@settings(max_examples=20, suppress_health_check=[HealthCheck.function_scoped_fixture])
@given(num_files=st.integers(1, 10))
@pytest.mark.asyncio
async def test_property_12_new_files(patch_summary, num_files):
    """Property 12: Incremental Indexing - New Files"""
    loader, chunker, embedder, store = _make_indexer_mocks()

    docs = []
    for i in range(num_files):
        docs.append(LoadedDocument(f"file_{i}.md", "content", datetime.now(), f"hash_{i}"))
    loader.load_documents.return_value = docs

    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    await indexer.run_indexing()

    assert indexer.files_indexed == num_files
    assert indexer.files_skipped == 0
    assert store.upsert_chunks.call_count == num_files

@settings(max_examples=20, suppress_health_check=[HealthCheck.function_scoped_fixture])
@given(num_files=st.integers(1, 10))
@pytest.mark.asyncio
async def test_property_13_modified_files(patch_summary, num_files):
    """Property 13: Incremental Indexing - Modified Files"""
    loader, chunker, embedder, store = _make_indexer_mocks()

    # DB has old hashes
    db_meta = [{"source_path": f"file_{i}.md", "content_hash": "old_hash"} for i in range(num_files)]
    store.get_all_metadata = AsyncMock(return_value=db_meta)

    # Disk has new hashes
    docs = []
    for i in range(num_files):
        docs.append(LoadedDocument(f"file_{i}.md", "content", datetime.now(), "new_hash"))
    loader.load_documents.return_value = docs

    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    await indexer.run_indexing()

    assert indexer.files_modified == num_files
    assert store.delete_by_path.call_count == num_files
    assert store.upsert_chunks.call_count == num_files

@settings(max_examples=20, suppress_health_check=[HealthCheck.function_scoped_fixture])
@given(num_files=st.integers(1, 10))
@pytest.mark.asyncio
async def test_property_14_deleted_files(patch_summary, num_files):
    """Property 14: Incremental Indexing - Deleted Files"""
    loader, chunker, embedder, store = _make_indexer_mocks()

    # DB has files
    db_meta = [{"source_path": f"deleted_{i}.md", "content_hash": "hash"} for i in range(num_files)]
    store.get_all_metadata = AsyncMock(return_value=db_meta)

    # Disk is empty
    loader.load_documents.return_value = []

    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    await indexer.run_indexing()

    assert indexer.files_deleted == num_files
    assert store.delete_by_path.call_count == num_files

@settings(max_examples=20, suppress_health_check=[HealthCheck.function_scoped_fixture])
@given(num_files=st.integers(1, 10))
@pytest.mark.asyncio
async def test_property_15_unchanged_files(patch_summary, num_files):
    """Property 15: Incremental Indexing - Unchanged Files"""
    loader, chunker, embedder, store = _make_indexer_mocks()

    # DB has files with same hashes
    db_meta = [{"source_path": f"file_{i}.md", "content_hash": "same_hash"} for i in range(num_files)]
    store.get_all_metadata = AsyncMock(return_value=db_meta)

    # Disk has same files with same hashes
    docs = []
    for i in range(num_files):
        docs.append(LoadedDocument(f"file_{i}.md", "content", datetime.now(), "same_hash"))
    loader.load_documents.return_value = docs

    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    await indexer.run_indexing()

    assert indexer.files_skipped == num_files
    assert store.upsert_chunks.call_count == 0
    assert store.delete_by_path.call_count == 0

@pytest.mark.asyncio
async def test_property_39_index_status_structure(patch_summary):
    """Property 39: IndexStatus Structure"""
    loader, chunker, embedder, store = _make_indexer_mocks()
    loader.load_documents.return_value = []

    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    await indexer.run_indexing()

    status = indexer.get_status()
    # Pydantic model verify
    assert hasattr(status, "total_files_indexed")
    assert hasattr(status, "total_chunks")
    assert hasattr(status, "last_index_run")
    assert hasattr(status, "pending_files")
    assert hasattr(status, "index_health")
    assert status.index_health == "healthy"
