"""Integration test for the full POST /brain/ai/query pipeline.

Exercises the entire RAG stack end-to-end using the FastAPI TestClient:
  AskRequest → Retriever → ContextAssembler → OllamaClient → StreamingResponse

All external I/O (Ollama HTTP, ChromaDB, filesystem) is mocked so the test
runs without Docker, but the wiring through FastAPI is real.
"""

import json

import pytest
from unittest.mock import AsyncMock, MagicMock, patch
from httpx import ASGITransport, AsyncClient

from brain.app.api import app


# ----------- helpers ----------- #

def _mock_app_state():
    """Attach mocked services to app.state so the router can find them."""
    app.state.embedding_pipeline = MagicMock()
    app.state.embedding_pipeline.ollama_url = "http://fake-ollama:11434"
    app.state.embedding_pipeline.embed_query = AsyncMock(return_value=[0.1] * 768)

    app.state.vector_store = MagicMock()
    # Return empty results → forces the threshold-fallback path
    app.state.vector_store.query = AsyncMock(
        return_value={"distances": [], "metadatas": [], "documents": []}
    )

    app.state.text_chunker = MagicMock()
    app.state.text_chunker._count_tokens = MagicMock(side_effect=lambda x: len(x))
    app.state.text_chunker.encoding = MagicMock()
    app.state.text_chunker.encoding.encode = MagicMock(side_effect=lambda x: list(x.encode()))
    app.state.text_chunker.encoding.decode = MagicMock(side_effect=lambda x: bytes(x).decode(errors="ignore"))

    app.state.document_loader = MagicMock()
    app.state.document_loader._extract_content = MagicMock(return_value=None)


# ----------- tests ----------- #

@pytest.mark.asyncio
async def test_streaming_pipeline_end_to_end():
    """POST /brain/ai/query with stream=true returns valid NDJSON with tokens + final answer."""

    _mock_app_state()

    # Mock Ollama streaming response
    async def _fake_aiter_lines():
        yield json.dumps({"response": "Hello", "done": False})
        yield json.dumps({"response": " world", "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 7})

    mock_resp = AsyncMock()
    mock_resp.raise_for_status = MagicMock()
    mock_resp.aiter_lines = _fake_aiter_lines

    mock_ctx = AsyncMock()
    mock_ctx.__aenter__ = AsyncMock(return_value=mock_resp)
    mock_ctx.__aexit__ = AsyncMock(return_value=False)

    with patch("brain.app.services.context_assembler.settings") as mock_settings, \
         patch("httpx.AsyncClient.stream", return_value=mock_ctx):
        mock_settings.vault_path = "/nonexistent"

        transport = ASGITransport(app=app)
        async with AsyncClient(transport=transport, base_url="http://test") as client:
            resp = await client.post(
                "/brain/ai/query",
                json={
                    "query": "What is JARVIS?",
                    "current_directory": ".",
                    "chat_history": [
                        {"role": "user", "content": "prior question"},
                        {"role": "assistant", "content": "prior answer"},
                    ],
                    "options": {"stream": True},
                },
                timeout=30.0,
            )

        assert resp.status_code == 200
        assert resp.headers["content-type"].startswith("application/x-ndjson")

        lines = [l for l in resp.text.strip().split("\n") if l.strip()]
        assert len(lines) >= 2, f"Expected at least 2 NDJSON lines, got {len(lines)}"

        # Parse every line as JSON
        parsed = [json.loads(line) for line in lines]

        # Token lines must contain "token" key
        token_lines = [p for p in parsed if "token" in p]
        assert len(token_lines) >= 1

        # Final line must contain "answer" key
        final = parsed[-1]
        assert "answer" in final, f"Last line missing 'answer': {final}"
        assert "Hello" in final["answer"]
        assert "sources" in final
        assert "model" in final
        assert "tokens_used" in final
        assert final["tokens_used"] == 7


@pytest.mark.asyncio
async def test_non_streaming_pipeline_end_to_end():
    """POST /brain/ai/query with stream=false returns a single AskResponse JSON."""

    _mock_app_state()

    async def _fake_aiter_lines():
        yield json.dumps({"response": "Direct answer.", "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 3})

    mock_resp = AsyncMock()
    mock_resp.raise_for_status = MagicMock()
    mock_resp.aiter_lines = _fake_aiter_lines

    mock_ctx = AsyncMock()
    mock_ctx.__aenter__ = AsyncMock(return_value=mock_resp)
    mock_ctx.__aexit__ = AsyncMock(return_value=False)

    with patch("brain.app.services.context_assembler.settings") as mock_settings, \
         patch("httpx.AsyncClient.stream", return_value=mock_ctx):
        mock_settings.vault_path = "/nonexistent"

        transport = ASGITransport(app=app)
        async with AsyncClient(transport=transport, base_url="http://test") as client:
            resp = await client.post(
                "/brain/ai/query",
                json={
                    "query": "Non-streaming test",
                    "options": {"stream": False},
                },
                timeout=30.0,
            )

        assert resp.status_code == 200
        data = resp.json()
        assert "answer" in data
        assert "Direct answer." in data["answer"]
        assert data["tokens_used"] == 3
        assert "sources" in data
        assert "model" in data


@pytest.mark.asyncio
async def test_chat_history_and_directory_forwarded():
    """Verify that chat_history and current_directory from the request actually
    reach the assembled prompt (not silently dropped)."""

    _mock_app_state()

    captured_prompts = []

    # Intercept the Ollama call to capture the prompt
    async def _fake_aiter_lines():
        yield json.dumps({"response": "ok", "done": True, "eval_count": 1})

    mock_resp = AsyncMock()
    mock_resp.raise_for_status = MagicMock()
    mock_resp.aiter_lines = _fake_aiter_lines

    real_stream = None

    class FakeStreamCtx:
        async def __aenter__(self):
            return mock_resp
        async def __aexit__(self, *args):
            pass

    def capture_stream(method, url, json=None):
        if json and "prompt" in json:
            captured_prompts.append(json["prompt"])
        return FakeStreamCtx()

    with patch("brain.app.services.context_assembler.settings") as mock_settings, \
         patch("httpx.AsyncClient.stream", side_effect=capture_stream):
        mock_settings.vault_path = "/nonexistent"

        transport = ASGITransport(app=app)
        async with AsyncClient(transport=transport, base_url="http://test") as client:
            resp = await client.post(
                "/brain/ai/query",
                json={
                    "query": "directory test",
                    "current_directory": "Projects/Alpha",
                    "chat_history": [
                        {"role": "user", "content": "UNIQUE_HISTORY_TOKEN_XYZ"},
                    ],
                    "options": {"stream": True},
                },
                timeout=30.0,
            )

        assert resp.status_code == 200
        assert len(captured_prompts) == 1

        prompt = captured_prompts[0]
        assert "Projects/Alpha" in prompt, "current_directory must appear in the prompt"
        assert "UNIQUE_HISTORY_TOKEN_XYZ" in prompt, "chat_history content must appear in the prompt"
