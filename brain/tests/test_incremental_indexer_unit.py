"""Unit tests for incremental indexer."""

import pytest
import asyncio
from unittest.mock import MagicMock, AsyncMock

from brain.app.services.incremental_indexer import IncrementalIndexer
from brain.app.services.document_loader import LoadedDocument
from datetime import datetime

@pytest.fixture
def mock_dependencies():
    loader = MagicMock()
    chunker = MagicMock()
    embedder = AsyncMock()
    store = AsyncMock()
    return loader, chunker, embedder, store

@pytest.mark.asyncio
async def test_concurrent_indexing(mock_dependencies):
    loader, chunker, embedder, store = mock_dependencies
    loader.load_documents.return_value = []
    store.get_all_metadata.return_value = []
    
    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    
    # Start first indexing
    indexer.is_indexing = True
    
    # Try second indexing
    await indexer.run_indexing()
    
    # Verify second one aborted immediately
    assert store.get_all_metadata.call_count == 0

@pytest.mark.asyncio
async def test_empty_vault(mock_dependencies):
    loader, chunker, embedder, store = mock_dependencies
    loader.load_documents.return_value = []
    store.get_all_metadata.return_value = []
    
    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    await indexer.run_indexing()
    
    assert indexer.files_indexed == 0
    assert indexer.files_skipped == 0
    assert indexer.files_deleted == 0
    assert store.upsert_chunks.call_count == 0

def test_start_background_indexing(mock_dependencies):
    loader, chunker, embedder, store = mock_dependencies
    indexer = IncrementalIndexer(loader, chunker, embedder, store)
    
    # Call directly
    indexer.start_background_indexing()
    
    assert indexer._indexing_task is not None
    assert not indexer._indexing_task.done()
    
    # Wait for completion slightly to clean up
    # We won't await in a simple test without event loop management, 
    # but the task creation is verified.
