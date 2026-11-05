"""nomic-embed-text needs search_document:/search_query: prefixes; other models must not get them."""
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from app.config import settings
from app.services.embedding_pipeline import EmbeddingPipeline
from app.services.text_chunker import Chunk


def _client(sent):
    async def post(url, json=None):
        sent.append(json)
        resp = MagicMock()
        resp.raise_for_status.return_value = None
        resp.json.return_value = {"embeddings": [[0.1] * 768 for _ in json["input"]]}
        return resp

    client = MagicMock()
    client.post = post
    client.aclose = AsyncMock()
    return client


def _chunk(text):
    return Chunk(chunk_id="i", source_path="p.md", chunk_index=0, total_chunks=1, content=text, content_hash="h")


@pytest.mark.asyncio
async def test_nomic_gets_document_and_query_prefixes(monkeypatch):
    monkeypatch.setattr(settings, "embedding_model", "nomic-embed-text")
    sent = []
    with patch("app.services.embedding_pipeline.httpx.AsyncClient", return_value=_client(sent)):
        pipeline = EmbeddingPipeline("http://fake")
        await pipeline.embed_chunks([_chunk("my note")])
        await pipeline.embed_query("my question")
    assert sent[0]["input"] == ["search_document: my note"]
    assert sent[1]["input"] == ["search_query: my question"]
    assert sent[0]["model"] == "nomic-embed-text"


@pytest.mark.asyncio
async def test_other_models_get_plain_text_and_the_configured_name(monkeypatch):
    monkeypatch.setattr(settings, "embedding_model", "some-other-embedder")
    sent = []
    with patch("app.services.embedding_pipeline.httpx.AsyncClient", return_value=_client(sent)):
        pipeline = EmbeddingPipeline("http://fake")
        await pipeline.embed_chunks([_chunk("my note")])
        await pipeline.embed_query("my question")
    assert sent[0]["input"] == ["my note"] and sent[1]["input"] == ["my question"]
    assert sent[0]["model"] == "some-other-embedder"
