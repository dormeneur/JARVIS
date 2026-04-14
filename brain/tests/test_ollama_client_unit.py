"""Unit tests for Ollama client."""

import json
import pytest
from unittest.mock import AsyncMock, MagicMock, patch

from brain.app.services.ollama_client import OllamaClient


@pytest.mark.asyncio
async def test_generate_non_streaming():
    mock_response = MagicMock()
    mock_response.json.return_value = {"response": "Hello", "eval_count": 2}
    mock_response.raise_for_status.return_value = None

    mock_client = MagicMock()
    mock_client.post = AsyncMock(return_value=mock_response)

    mock_client_ctx = AsyncMock()
    mock_client_ctx.__aenter__ = AsyncMock(return_value=mock_client)
    mock_client_ctx.__aexit__ = AsyncMock(return_value=False)

    with patch("httpx.AsyncClient", return_value=mock_client_ctx):
        client = OllamaClient("http://fake")
        text, tokens = await client.generate("Say hello")

        assert text == "Hello"
        assert tokens == 2


@pytest.mark.asyncio
async def test_generate_streaming_error_handling():
    mock_response = MagicMock()
    mock_response.raise_for_status.side_effect = Exception("Connection refused")

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

        errors = []
        async for token in client.generate_streaming("prompt"):
            if token.startswith('{"__jarvis_error__"'):
                errors.append(json.loads(token))

        assert len(errors) == 1
        assert "Connection refused" in errors[0]["__jarvis_error__"]
