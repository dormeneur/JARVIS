"""Regression: an unchanged vault must not be re-embedded or re-summarised on every run.

The old indexer stored each chunk's own hash but compared it with the file hash, so nothing
ever matched. These tests use the real loader and chunker and a store that keeps what it's given.
"""
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from app.services import incremental_indexer as ii
from app.services.document_loader import DocumentLoader
from app.services.incremental_indexer import IncrementalIndexer
from app.services.text_chunker import TextChunker


class FakeVectorStore:
    def __init__(self):
        self.meta = {}  # chunk_id -> metadata
        self.upserts = 0
        self.deletes = []

    async def upsert_chunks(self, chunks, embeddings, last_modified, summary="", file_hash="", index_version=0):
        self.upserts += 1
        for c in chunks:
            self.meta[c.chunk_id] = {
                "source_path": c.source_path, "content_hash": c.content_hash,
                "file_hash": file_hash, "index_version": index_version,
            }

    async def delete_by_path(self, path):
        self.deletes.append(path)
        self.meta = {k: v for k, v in self.meta.items() if v["source_path"] != path}

    async def get_all_metadata(self):
        return list(self.meta.values())

    async def count(self):
        return len(self.meta)


@pytest.fixture
def setup(tmp_path):
    (tmp_path / "a.md").write_text("alpha note", encoding="utf-8")
    (tmp_path / "b.md").write_text("beta note", encoding="utf-8")
    (tmp_path / "c.md").write_text(("long paragraph about gamma. " * 200 + "\n\n") * 6, encoding="utf-8")
    embedder = MagicMock()
    embedder.ollama_url = "http://x"
    embedder.embed_chunks = AsyncMock(side_effect=lambda chunks: [[0.1] * 768 for _ in chunks])
    store = FakeVectorStore()
    indexer = IncrementalIndexer(DocumentLoader(str(tmp_path)), TextChunker(), embedder, store)
    gen = AsyncMock(return_value=("Summary.", 5))
    with patch("app.services.incremental_indexer.OllamaClient") as client, \
            patch.object(IncrementalIndexer, "_update_global_memory", new_callable=AsyncMock):
        client.return_value.generate = gen
        yield tmp_path, indexer, store, gen


@pytest.mark.asyncio
async def test_second_run_does_nothing(setup):
    _, indexer, store, gen = setup
    await indexer.run_indexing()
    assert store.upserts == 3
    first_summaries = gen.call_count
    store.upserts = 0
    indexer.files_skipped = 0
    await indexer.run_indexing()
    assert store.upserts == 0
    assert gen.call_count == first_summaries
    assert indexer.files_skipped == 3


@pytest.mark.asyncio
async def test_edit_reindexes_only_that_file(setup):
    tmp, indexer, store, _ = setup
    await indexer.run_indexing()
    store.upserts, store.deletes = 0, []
    (tmp / "a.md").write_text("alpha note, edited", encoding="utf-8")
    await indexer.run_indexing()
    assert store.upserts == 1
    assert store.deletes == ["a.md"]


@pytest.mark.asyncio
async def test_deleted_file_is_removed(setup):
    tmp, indexer, store, _ = setup
    await indexer.run_indexing()
    store.deletes = []
    (tmp / "b.md").unlink()
    await indexer.run_indexing()
    assert store.deletes == ["b.md"]


@pytest.mark.asyncio
async def test_index_version_bump_reindexes_everything(setup):
    _, indexer, store, _ = setup
    await indexer.run_indexing()
    store.upserts = 0
    with patch.object(ii, "INDEX_VERSION", ii.INDEX_VERSION + 1):
        await indexer.run_indexing()
    assert store.upserts == 3


@pytest.mark.asyncio
async def test_generated_global_index_is_not_indexed(setup):
    """Memory/global_index.md summarises every file including itself: indexing it would loop forever."""
    tmp, indexer, store, _ = setup
    (tmp / "Memory").mkdir()
    (tmp / "Memory" / "global_index.md").write_text("- a.md: alpha", encoding="utf-8")
    await indexer.run_indexing()
    assert all(m["source_path"].replace("\\", "/") != "Memory/global_index.md" for m in store.meta.values())


@pytest.mark.asyncio
async def test_digest_summaries_are_not_indexed_but_a_nested_digest_folder_is(setup):
    """Model-written digests must not be retrievable as evidence; a user's own deeper 'Digest' folder is theirs."""
    tmp, indexer, store, _ = setup
    (tmp / "Digest").mkdir()
    (tmp / "Digest" / "2026-11-05.md").write_text("a model's paraphrase of your notes", encoding="utf-8")
    (tmp / "Reading" / "Digest").mkdir(parents=True)
    (tmp / "Reading" / "Digest" / "mine.md").write_text("my own reading digest", encoding="utf-8")
    await indexer.run_indexing()
    paths = {m["source_path"].replace("\\", "/") for m in store.meta.values()}
    assert "Digest/2026-11-05.md" not in paths
    assert "Reading/Digest/mine.md" in paths
