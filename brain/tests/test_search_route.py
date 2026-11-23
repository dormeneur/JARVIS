"""POST /brain/ai/search: ranked passages with no model call."""

import asyncio
from types import SimpleNamespace

import pytest
from fastapi import HTTPException
from pydantic import ValidationError

from app.models.ask_models import SearchRequest
from app.routers import ask as ask_router


class FakeRetriever:
    calls = []

    def __init__(self, *a, **kw):
        pass

    async def retrieve(self, query=None, top_k=5, **kw):
        FakeRetriever.calls.append((query, top_k))
        if query == "boom":
            raise RuntimeError("index down")
        return [SimpleNamespace(path="General/game.md", score=0.9, content="x" * 5000)]


def _request():
    state = SimpleNamespace(embedding_pipeline=None, vector_store=None, keyword_index=None)
    return SimpleNamespace(app=SimpleNamespace(state=state))


def test_returns_ranked_passages_capped(monkeypatch):
    monkeypatch.setattr(ask_router, "Retriever", FakeRetriever)
    out = asyncio.run(ask_router.search_brain(SearchRequest(query="favourite game", top_k=3), _request()))
    assert out["results"][0]["path"] == "General/game.md"
    assert len(out["results"][0]["content"]) == 1200  # a passage is capped so one hit can't flood a prompt
    assert FakeRetriever.calls[-1] == ("favourite game", 3)


def test_index_failure_is_503_not_a_crash(monkeypatch):
    monkeypatch.setattr(ask_router, "Retriever", FakeRetriever)
    with pytest.raises(HTTPException) as err:
        asyncio.run(ask_router.search_brain(SearchRequest(query="boom"), _request()))
    assert err.value.status_code == 503


def test_request_is_validated():
    for bad in ({"query": ""}, {"query": "x", "top_k": 99}):
        with pytest.raises(ValidationError):
            SearchRequest(**bad)
