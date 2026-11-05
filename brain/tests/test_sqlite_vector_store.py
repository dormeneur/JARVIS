"""The SQLite vector store must behave like VectorStore (ChromaDB) for everything the brain uses."""
import asyncio
from unittest.mock import AsyncMock, MagicMock, patch

import numpy as np
import pytest

from app.config import settings
from app.services.document_loader import DocumentLoader
from app.services.incremental_indexer import IncrementalIndexer
from app.services.retriever import Retriever
from app.services.sqlite_vector_store import SqliteVectorStore, migrate_from_chroma
from app.services.text_chunker import Chunk, TextChunker


def run(coro):
    return asyncio.run(coro)


def chunk(cid, path, text, index=0):
    return Chunk(chunk_id=cid, source_path=path, chunk_index=index, total_chunks=1, content=text, content_hash="h")


def vec(*head, size=768):
    v = np.zeros(size, dtype=np.float32)
    v[: len(head)] = head
    return v.tolist()


@pytest.fixture
def store(tmp_path):
    return SqliteVectorStore(tmp_path / ".system" / "brain" / "vectors.db")


def test_nearest_first_in_chromas_response_shape(store):
    run(store.upsert_chunks(
        [chunk("a", "a.md", "alpha"), chunk("b", "b.md", "beta"), chunk("c", "c.md", "gamma")],
        [vec(1, 0), vec(0.9, 0.1), vec(0, 1)], "t", summary="s", file_hash="h", index_version=2))
    out = run(store.query(vec(1, 0), top_k=2))
    assert out["ids"] == [["a", "b"]]
    assert out["documents"] == [["alpha", "beta"]]
    assert out["metadatas"][0][0]["source_path"] == "a.md" and out["metadatas"][0][0]["file_hash"] == "h"
    d = out["distances"][0]
    assert d[0] == pytest.approx(0.0, abs=1e-5) and 0 < d[1] < 0.1  # cosine distance, 0 = identical


def test_scale_of_the_vector_does_not_matter(store):
    run(store.upsert_chunks([chunk("a", "a.md", "x")], [vec(5, 0)], "t"))
    assert run(store.query(vec(0.01, 0), top_k=1))["distances"][0][0] == pytest.approx(0.0, abs=1e-5)


def test_filter_paths_delete_and_replace(store):
    run(store.upsert_chunks([chunk("a", "a.md", "alpha"), chunk("b", "b.md", "beta")], [vec(1, 0), vec(1, 0)], "t"))
    assert run(store.query(vec(1, 0), 5, filter_paths=["b.md"]))["ids"] == [["b"]]
    assert run(store.query(vec(1, 0), 5, filter_paths=["nope.md"]))["ids"] == [[]]
    run(store.upsert_chunks([chunk("a", "a.md", "alpha v2")], [vec(0, 1)], "t"))   # same id replaces
    assert run(store.count()) == 2
    assert run(store.query(vec(0, 1), 1))["documents"] == [["alpha v2"]]
    run(store.delete_by_path("a.md"))
    assert run(store.count()) == 1 and run(store.query(vec(1, 0), 5))["ids"] == [["b"]]


def test_empty_store_and_bad_input(store):
    assert run(store.query(vec(1), 5))["ids"] == [[]]
    run(store.upsert_chunks([], [], "t"))
    with pytest.raises(ValueError):
        run(store.upsert_chunks([chunk("a", "a.md", "x")], [vec(1), vec(2)], "t"))


def test_survives_reopening_the_file(tmp_path):
    path = tmp_path / "v.db"
    run(SqliteVectorStore(path).upsert_chunks([chunk("a", "a.md", "alpha")], [vec(1)], "t"))
    again = SqliteVectorStore(path)
    assert run(again.count()) == 1 and run(again.query(vec(1), 1))["ids"] == [["a"]]


def test_metadata_listing_and_iter_all(store):
    run(store.upsert_chunks([chunk("a", "a.md", "alpha")], [vec(1)], "t", file_hash="h", index_version=2))
    assert run(store.get_all_metadata())[0]["file_hash"] == "h"

    async def collect():
        return [x async for x in store.iter_all()]

    [(cid, doc, meta)] = run(collect())
    assert (cid, doc, meta["source_path"]) == ("a", "alpha", "a.md")


def test_migration_from_chroma_copies_embeddings_without_the_model(store):
    class FakeChroma:
        async def iter_all_with_embeddings(self):
            yield "a", "alpha", {"source_path": "a.md", "chunk_index": 0, "file_hash": "h", "index_version": 2}, vec(1, 0)
            yield "b", "beta", {"source_path": "b.md", "chunk_index": 0, "file_hash": "h", "index_version": 2}, vec(0, 1)

    assert run(migrate_from_chroma(FakeChroma(), store, batch=1)) == 2
    assert run(store.query(vec(0, 1), 1))["ids"] == [["b"]]
    assert {m["file_hash"] for m in run(store.get_all_metadata())} == {"h"}  # so the indexer sees it as up to date


def test_retriever_works_unchanged_on_top_of_it(store, tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "hybrid_search", False)
    run(store.upsert_chunks([chunk("a", "People/raj.md", "Raj is my manager"), chunk("b", "Notes/x.md", "groceries")],
                            [vec(1, 0), vec(0, 1)], "t"))
    embedder = MagicMock()
    embedder.embed_query = AsyncMock(return_value=vec(1, 0.05))
    [top] = run(Retriever(embedder, store).retrieve("who is Raj", top_k=1))
    assert top.path == "People/raj.md" and top.content == "Raj is my manager" and top.score > 0.9


def test_indexer_keeps_it_up_to_date_and_skips_unchanged_files(tmp_path, store):
    vault = tmp_path / "vault"
    vault.mkdir()
    (vault / "a.md").write_text("alpha note", encoding="utf-8")
    embedder = MagicMock()
    embedder.ollama_url = "http://x"
    embedder.embed_chunks = AsyncMock(side_effect=lambda cs: [vec(1, 0) for _ in cs])
    indexer = IncrementalIndexer(DocumentLoader(str(vault)), TextChunker(), embedder, store)
    with patch("app.services.incremental_indexer.OllamaClient") as client, \
            patch.object(IncrementalIndexer, "_update_global_memory", new_callable=AsyncMock):
        client.return_value.generate = AsyncMock(return_value=("S.", 1))
        run(indexer.run_indexing())
        assert run(store.count()) == 1
        embedder.embed_chunks.reset_mock()
        run(indexer.run_indexing())
        embedder.embed_chunks.assert_not_called()          # unchanged: nothing re-embedded
        (vault / "a.md").unlink()
        run(indexer.run_indexing())
    assert run(store.count()) == 0
