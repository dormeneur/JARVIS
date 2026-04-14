"""Property-based tests for /brain/ai/query endpoint."""

import pytest
import json
from unittest.mock import AsyncMock, MagicMock, patch
from hypothesis import given, settings, strategies as st

from brain.app.routers.ask import generate_rag_stream
from brain.app.models.ask_models import AskRequest, AskOptions


def _make_app_state():
    """Create a mocked application state with all required services."""
    state = MagicMock()
    state.embedding_pipeline = MagicMock()
    state.embedding_pipeline.ollama_url = "http://fake"
    state.embedding_pipeline.embed_query = AsyncMock(return_value=[0.1]*768)
    state.vector_store = MagicMock()
    state.vector_store.query = AsyncMock(
        return_value={"distances": [], "metadatas": [], "documents": []}
    )
    state.text_chunker = MagicMock()
    state.text_chunker._count_tokens = MagicMock(side_effect=lambda x: len(x))
    state.text_chunker.encoding = MagicMock()
    state.text_chunker.encoding.encode = MagicMock(side_effect=lambda x: list(x.encode()))
    state.text_chunker.encoding.decode = MagicMock(side_effect=lambda x: bytes(x).decode(errors='ignore'))
    state.document_loader = MagicMock()
    state.document_loader._extract_content = MagicMock(return_value=None)
    return state


@settings(max_examples=20)
@given(tokens=st.lists(st.text(min_size=1, max_size=10), min_size=1, max_size=5))
@pytest.mark.asyncio
async def test_property_29_streaming_response_format(tokens):
    """Property 29: Streaming Response Format

    **Validates: Requirement 10.7**
    Verify response is NDJSON format with streaming tokens.
    """
    async def _fake_aiter_lines():
        for t in tokens:
            yield json.dumps({"response": t, "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 42})

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

        app_state = _make_app_state()
        req = AskRequest(query="test", options=AskOptions(stream=True))

        stream_results = []
        async for chunk in generate_rag_stream(req, app_state):
            stream_results.append(chunk)

    # Verify all lines are JSON loadable
    parsed = [json.loads(line) for line in stream_results]

    # We yield {"token": t} for each token
    extracted_tokens = [p["token"] for p in parsed if "token" in p]
    assert extracted_tokens == tokens

    # Final should be AskResponse
    assert "answer" in parsed[-1]


@settings(max_examples=20)
@given(query=st.text(min_size=1, max_size=20))
@pytest.mark.asyncio
async def test_property_30_complete_response_structure(query):
    """Property 30: Complete Response Structure

    **Validates: Requirement 10.8**
    Verify final AskResponse includes answer, sources, model, tokens_used.
    """
    async def _fake_aiter_lines():
        yield json.dumps({"response": "FinalAnswer", "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 99})

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

        app_state = _make_app_state()
        req = AskRequest(query=query)
        stream_results = []
        async for chunk in generate_rag_stream(req, app_state):
            stream_results.append(chunk)

    final_dict = json.loads(stream_results[-1])

    assert "answer" in final_dict
    assert final_dict["answer"] == "FinalAnswer"
    assert "sources" in final_dict
    assert "model" in final_dict
    assert "tokens_used" in final_dict
    assert final_dict["tokens_used"] == 99
