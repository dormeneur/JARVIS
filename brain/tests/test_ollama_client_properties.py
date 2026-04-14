"""Property-based tests for Ollama client.

Feature: phase-3-ai-integration
Properties tested:
- Property 27: NDJSON Parsing
- Property 28: Token Count Extraction
"""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock, patch
from hypothesis import given, settings, strategies as st

from brain.app.services.ollama_client import OllamaClient


@settings(max_examples=50)
@given(responses=st.lists(st.text(min_size=1, max_size=20), min_size=1, max_size=10))
@pytest.mark.asyncio
async def test_property_27_ndjson_parsing(responses):
    """Property 27: NDJSON Parsing"""

    # Create valid NDJSON stream mock
    async def mock_aiter_lines():
        for r in responses:
            yield json.dumps({"response": r, "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 42})

    mock_response = MagicMock()
    mock_response.raise_for_status = MagicMock()
    mock_response.aiter_lines = mock_aiter_lines

    # Mock the async context manager for client.stream()
    mock_stream_ctx = AsyncMock()
    mock_stream_ctx.__aenter__ = AsyncMock(return_value=mock_response)
    mock_stream_ctx.__aexit__ = AsyncMock(return_value=False)

    # Mock the async context manager for httpx.AsyncClient()
    mock_client = MagicMock()
    mock_client.stream.return_value = mock_stream_ctx

    mock_client_ctx = AsyncMock()
    mock_client_ctx.__aenter__ = AsyncMock(return_value=mock_client)
    mock_client_ctx.__aexit__ = AsyncMock(return_value=False)

    with patch("httpx.AsyncClient", return_value=mock_client_ctx):
        client = OllamaClient("http://fake")
        tokens = []
        metadata = None

        async for token in client.generate_streaming("prompt"):
            if token.startswith('{"__jarvis_metadata__"'):
                metadata = json.loads(token)
            else:
                tokens.append(token)

        assert tokens == responses
        assert metadata is not None


@settings(max_examples=50)
@given(eval_count=st.integers(1, 1000))
@pytest.mark.asyncio
async def test_property_28_token_count(eval_count):
    """Property 28: Token Count extraction"""

    async def mock_aiter_lines():
        yield json.dumps({"response": "done", "done": True, "eval_count": eval_count})

    mock_response = MagicMock()
    mock_response.raise_for_status = MagicMock()
    mock_response.aiter_lines = mock_aiter_lines

    mock_stream_ctx = AsyncMock()
    mock_stream_ctx.__aenter__ = AsyncMock(return_value=mock_response)
    mock_stream_ctx.__aexit__ = AsyncMock(return_value=False)

    mock_client = MagicMock()
    mock_client.stream.return_value = mock_stream_ctx

    mock_client_ctx = AsyncMock()
    mock_client_ctx.__aenter__ = AsyncMock(return_value=mock_client)
    mock_client_ctx.__aexit__ = AsyncMock(return_value=False)

    with patch("httpx.AsyncClient", return_value=mock_client_ctx):
        client = OllamaClient("http://fake")
        metadata = None

        async for token in client.generate_streaming("prompt"):
            if token.startswith('{"__jarvis_metadata__"'):
                metadata = json.loads(token)

        assert metadata is not None
        assert metadata["eval_count"] == eval_count
