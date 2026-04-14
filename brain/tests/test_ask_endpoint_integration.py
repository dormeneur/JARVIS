"""Integration tests for POST /brain/ai/query."""

import pytest
import json
from unittest.mock import AsyncMock, MagicMock, patch
from fastapi import Request

from brain.app.routers.ask import ask_brain
from brain.app.models.ask_models import AskRequest, AskOptions

@pytest.fixture
def mock_request():
    request = AsyncMock(spec=Request)
    request.app = MagicMock()
    request.app.state = MagicMock()
    request.app.state.embedding_pipeline = MagicMock()
    request.app.state.embedding_pipeline.ollama_url = "http://fake"
    request.app.state.embedding_pipeline.embed_query = AsyncMock(return_value=[0.1]*768)
    request.app.state.vector_store = MagicMock()
    request.app.state.vector_store.query = AsyncMock(
        return_value={"distances": [], "metadatas": [], "documents": []}
    )
    request.app.state.text_chunker = MagicMock()
    request.app.state.text_chunker._count_tokens = MagicMock(side_effect=lambda x: len(x))
    request.app.state.text_chunker.encoding = MagicMock()
    request.app.state.text_chunker.encoding.encode = MagicMock(side_effect=lambda x: list(x.encode()))
    request.app.state.text_chunker.encoding.decode = MagicMock(side_effect=lambda x: bytes(x).decode(errors='ignore'))
    request.app.state.document_loader = MagicMock()
    request.app.state.document_loader._extract_content = MagicMock(return_value=None)
    return request

@pytest.fixture
def patch_ollama_stream():
    async def _fake_aiter_lines():
        yield json.dumps({"response": "Hello ", "done": False})
        yield json.dumps({"response": "world.", "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 2})

    mock_resp = MagicMock()
    mock_resp.raise_for_status = MagicMock()
    mock_resp.aiter_lines = _fake_aiter_lines

    mock_stream_ctx = AsyncMock()
    mock_stream_ctx.__aenter__ = AsyncMock(return_value=mock_resp)
    mock_stream_ctx.__aexit__ = AsyncMock(return_value=False)

    mock_client = MagicMock()
    mock_client.stream.return_value = mock_stream_ctx

    mock_client_ctx = AsyncMock()
    mock_client_ctx.__aenter__ = AsyncMock(return_value=mock_client)
    mock_client_ctx.__aexit__ = AsyncMock(return_value=False)

    with patch("brain.app.services.context_assembler.settings") as mock_settings, \
         patch("httpx.AsyncClient", return_value=mock_client_ctx):
        mock_settings.vault_path = "/nonexistent"
        yield


@pytest.mark.asyncio
async def test_ask_non_streaming(mock_request, patch_ollama_stream):
    req = AskRequest(query="test", options=AskOptions(stream=False))

    response = await ask_brain(req, mock_request)

    assert response.answer == "Hello world."
    assert response.tokens_used == 2

@pytest.mark.asyncio
async def test_ask_streaming_response_type(mock_request, patch_ollama_stream):
    from fastapi.responses import StreamingResponse
    req = AskRequest(query="test", options=AskOptions(stream=True))

    response = await ask_brain(req, mock_request)

    assert isinstance(response, StreamingResponse)
    assert response.media_type == "application/x-ndjson"
