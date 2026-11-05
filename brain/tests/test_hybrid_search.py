"""Keyword index (FTS5), RRF fusion, hybrid retrieval, and keeping Secrets out of the keyword index."""
import asyncio
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from app.config import settings
from app.services.document_loader import DocumentLoader
from app.services.incremental_indexer import IncrementalIndexer
from app.services.keyword_index import KeywordIndex, fts_query, query_tokens
from app.services.retriever import Retriever, cap_per_file, rrf_fuse
from app.services.text_chunker import Chunk, TextChunker


def _chunk(cid, path, text, index=0):
    return Chunk(chunk_id=cid, source_path=path, chunk_index=index, total_chunks=1, content=text, content_hash="h")


def run(coro):
    return asyncio.run(coro)


# --- fusion ---------------------------------------------------------------------------------

def test_rrf_rewards_agreement_between_lists():
    fused = rrf_fuse([["a", "b", "c"], ["c", "a", "d"]])
    ids = [i for i, _ in fused]
    assert ids[:2] == ["a", "c"]  # in both lists beats in one
    assert set(ids) == {"a", "b", "c", "d"}


def test_rrf_single_list_keeps_its_order_and_empty_is_empty():
    assert [i for i, _ in rrf_fuse([["x", "y", "z"]])] == ["x", "y", "z"]
    assert rrf_fuse([[], []]) == []


def test_cap_per_file_keeps_order_and_limit():
    items = [{"path": "a"}, {"path": "a"}, {"path": "a"}, {"path": "b"}]
    assert cap_per_file(items, 2) == [{"path": "a"}, {"path": "a"}, {"path": "b"}]


# --- keyword index --------------------------------------------------------------------------

@pytest.fixture
def kw(tmp_path):
    return KeywordIndex(tmp_path / ".system" / "brain" / "fts.db")


def test_query_tokens_drop_stopwords_and_short_words():
    assert query_tokens("What is the passport number of Raj?") == ["passport", "number", "raj"]
    assert fts_query("???") == ""


def test_search_finds_exact_names_and_ranks(kw):
    run(kw.upsert([
        _chunk("1", "People/raj.md", "Raj Malhotra is my manager at Acme"),
        _chunk("2", "Notes/misc.md", "grocery list: milk, eggs"),
    ]))
    hits = run(kw.search("who is Malhotra"))
    assert [h["path"] for h in hits] == ["People/raj.md"]
    assert run(kw.count()) == 2


def test_search_survives_fts_syntax_in_the_question(kw):
    run(kw.upsert([_chunk("1", "a.md", "alpha beta")]))
    for nasty in ['foo" AND (', "alpha OR NOT", "*", "NEAR(", "a\"b'c", ""]:
        run(kw.search(nasty))  # must not raise


def test_filter_paths_delete_and_reupsert(kw):
    run(kw.upsert([_chunk("1", "a.md", "needle here"), _chunk("2", "b.md", "needle there")]))
    assert {h["path"] for h in run(kw.search("needle"))} == {"a.md", "b.md"}
    assert [h["path"] for h in run(kw.search("needle", filter_paths=["b.md"]))] == ["b.md"]
    run(kw.delete_by_path("a.md"))
    assert [h["path"] for h in run(kw.search("needle"))] == ["b.md"]
    run(kw.upsert([_chunk("2", "b.md", "changed words")]))  # same id replaces, no duplicate
    assert run(kw.count()) == 1 and run(kw.search("needle")) == []


# --- hybrid retrieval -----------------------------------------------------------------------

def _store_returning(ids, distances, paths, docs):
    store = MagicMock()
    store.query = AsyncMock(return_value={
        "ids": [ids], "distances": [distances], "documents": [docs],
        "metadatas": [[{"source_path": p, "chunk_index": 0} for p in paths]],
    })
    return store


def _embedder():
    e = MagicMock()
    e.embed_query = AsyncMock(return_value=[0.1] * 768)
    return e


def test_exact_name_found_only_by_keyword_is_returned(kw, monkeypatch):
    monkeypatch.setattr(settings, "hybrid_search", True)
    run(kw.upsert([_chunk("k1", "People/raj.md", "Raj Malhotra is my manager")]))
    store = _store_returning(["v1"], [0.4], ["Notes/other.md"], ["unrelated vector hit"])
    results = run(Retriever(_embedder(), store, kw).retrieve("Malhotra", top_k=5))
    paths = [r.path for r in results]
    assert "People/raj.md" in paths and "Notes/other.md" in paths
    assert all(0 <= r.score <= 1 for r in results) and max(r.score for r in results) == 1.0


def test_hybrid_allows_two_chunks_per_file_but_not_three(kw, monkeypatch):
    monkeypatch.setattr(settings, "hybrid_search", True)
    run(kw.upsert([_chunk(f"k{i}", "big.md", f"zebra part {i}", i) for i in range(4)]))
    store = _store_returning([], [], [], [])
    store.query = AsyncMock(return_value={"ids": [[]], "distances": [[]], "documents": [[]], "metadatas": [[]]})
    results = run(Retriever(_embedder(), store, kw).retrieve("zebra", top_k=5))
    assert len(results) == 2 and {r.path for r in results} == {"big.md"}


def test_hybrid_off_or_no_index_is_plain_vector_search(kw, monkeypatch):
    run(kw.upsert([_chunk("k1", "People/raj.md", "Raj Malhotra")]))
    store = _store_returning(["v1"], [0.1], ["Notes/a.md"], ["text"])
    monkeypatch.setattr(settings, "hybrid_search", False)
    assert [r.path for r in run(Retriever(_embedder(), store, kw).retrieve("Malhotra"))] == ["Notes/a.md"]
    monkeypatch.setattr(settings, "hybrid_search", True)
    assert [r.path for r in run(Retriever(_embedder(), store, None).retrieve("Malhotra"))] == ["Notes/a.md"]


def test_punctuation_only_query_still_gets_vector_results(kw, monkeypatch):
    monkeypatch.setattr(settings, "hybrid_search", True)
    store = _store_returning(["v1"], [0.1], ["Notes/a.md"], ["text"])
    assert [r.path for r in run(Retriever(_embedder(), store, kw).retrieve("???"))] == ["Notes/a.md"]


# --- indexer integration + secrets ----------------------------------------------------------

class FakeStore:
    def __init__(self):
        self.rows = {}

    async def upsert_chunks(self, chunks, embeddings, last_modified, summary="", file_hash="", index_version=0):
        for c in chunks:
            self.rows[c.chunk_id] = (c, {"source_path": c.source_path, "chunk_index": c.chunk_index,
                                         "file_hash": file_hash, "index_version": index_version})

    async def delete_by_path(self, path):
        self.rows = {k: v for k, v in self.rows.items() if v[1]["source_path"] != path}

    async def get_all_metadata(self):
        return [m for _, m in self.rows.values()]

    async def count(self):
        return len(self.rows)

    async def iter_all(self, batch=500):
        for cid, (c, m) in list(self.rows.items()):
            yield cid, c.content, m


def _indexer(vault, kw, store=None):
    embedder = MagicMock()
    embedder.ollama_url = "http://x"
    embedder.embed_chunks = AsyncMock(side_effect=lambda cs: [[0.1] * 768 for _ in cs])
    return IncrementalIndexer(DocumentLoader(str(vault)), TextChunker(), embedder, store or FakeStore(), kw)


@pytest.fixture
def no_llm():
    with patch("app.services.incremental_indexer.OllamaClient") as client, \
            patch.object(IncrementalIndexer, "_update_global_memory", new_callable=AsyncMock):
        client.return_value.generate = AsyncMock(return_value=("S.", 1))
        yield


def test_keyword_index_follows_edits_and_never_holds_secrets(tmp_path, kw, no_llm):
    vault = tmp_path / "vault"
    (vault / "Notes").mkdir(parents=True)
    (vault / "Secrets").mkdir()
    (vault / "Notes" / "a.md").write_text("alpha unicorn note", encoding="utf-8")
    (vault / "Secrets" / "k.txt").write_text("classified unicorn password", encoding="utf-8")
    indexer = _indexer(vault, kw)

    run(indexer.run_indexing())
    assert [h["path"].replace("\\", "/") for h in run(kw.search("unicorn"))] == ["Notes/a.md"]
    assert run(kw.search("classified")) == []

    (vault / "Notes" / "a.md").write_text("beta narwhal note", encoding="utf-8")
    run(indexer.run_indexing())
    assert run(kw.search("unicorn")) == [] and len(run(kw.search("narwhal"))) == 1

    (vault / "Notes" / "a.md").unlink()
    run(indexer.run_indexing())
    assert run(kw.count()) == 0


def test_out_of_step_keyword_index_is_rebuilt_from_the_vector_store(tmp_path, kw, no_llm):
    vault = tmp_path / "vault"
    vault.mkdir()
    store = FakeStore()
    run(store.upsert_chunks([_chunk("c1", "Notes/a.md", "persistent walrus")], [[0.1]], "t", file_hash="h", index_version=2))
    run(store.upsert_chunks([_chunk("c2", "Secrets/x.md", "legacy leaked walrus")], [[0.1]], "t", file_hash="h", index_version=2))
    assert run(kw.count()) == 0
    run(_indexer(vault, kw, store)._sync_keyword_index())
    assert [h["path"] for h in run(kw.search("walrus"))] == ["Notes/a.md"]
