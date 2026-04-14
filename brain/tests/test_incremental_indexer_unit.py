"""Unit tests for incremental indexer."""

import pytest
import asyncio
from unittest.mock import MagicMock, AsyncMock, patch

from brain.app.services.incremental_indexer import IncrementalIndexer
from brain.app.services.document_loader import LoadedDocument
from datetime import datetime

@pytest.fixture
def mock_dependencies():
    loader = MagicMock()
    chunker = MagicMock()
    embedder = MagicMock()
    store = MagicMock()

    embedder.ollama_url = "http://fake"
    embedder.embed_chunks = AsyncMock(return_value=[[0.1]*768])
    store.get_all_metadata = AsyncMock(return_value=[])
    store.upsert_chunks = AsyncMock()
    store.delete_by_path = AsyncMock()

    return loader, chunker, embedder, store


@pytest.fixture
def patch_summary():
    """Patch OllamaClient and global memory to avoid real I/O."""
    with patch("brain.app.services.incremental_indexer.OllamaClient") as mock_cls:
        mock_instance = MagicMock()
        mock_instance.generate = AsyncMock(return_value=("Summary.", 5))
        mock_cls.return_value = mock_instance
        with patch.object(IncrementalIndexer, "_update_global_memory", new_callable=AsyncMock):
            yield


@pytest.mark.asyncio
async def test_concurrent_indexing(mock_dependencies, patch_summary):
    loader, chunker, embedder, store = mock_dependencies
    loader.load_documents.return_value = []

    indexer = IncrementalIndexer(loader, chunker, embedder, store)

    # Start first indexing
    indexer.is_indexing = True

    # Try second indexing
    await indexer.run_indexing()

    # Verify second one aborted immediately
    assert store.get_all_metadata.call_count == 0

@pytest.mark.asyncio
async def test_empty_vault(mock_dependencies, patch_summary):
    loader, chunker, embedder, store = mock_dependencies
    loader.load_documents.return_value = []

    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    await indexer.run_indexing()

    assert indexer.files_indexed == 0
    assert indexer.files_skipped == 0
    assert indexer.files_deleted == 0
    assert store.upsert_chunks.call_count == 0

@pytest.mark.asyncio
async def test_start_background_indexing(mock_dependencies, patch_summary):
    loader, chunker, embedder, store = mock_dependencies
    loader.load_documents.return_value = []

    indexer = IncrementalIndexer(loader, chunker, embedder, store)

    # Call directly
    indexer.start_background_indexing()

    assert indexer._indexing_task is not None
    # Wait for it to complete
    await indexer._indexing_task
    assert indexer._indexing_task.done()
