"""Unit tests for Ollama client."""

import json
import pytest
from unittest.mock import AsyncMock, patch

from brain.app.services.ollama_client import OllamaClient

@pytest.fixture
def mock_httpx_post():
    with patch("httpx.AsyncClient.post", new_callable=AsyncMock) as mock_post:
        yield mock_post
        
@pytest.fixture
def mock_httpx_stream():
    with patch("httpx.AsyncClient.stream") as mock_stream:
        mock_ctx = AsyncMock()
        mock_response = AsyncMock()
        mock_ctx.__aenter__.return_value = mock_response
        mock_stream.return_value = mock_ctx
        yield mock_response

@pytest.mark.asyncio
async def test_generate_non_streaming(mock_httpx_post):
    mock_response = AsyncMock()
    mock_response.json.return_value = {"response": "Hello", "eval_count": 2}
    mock_response.raise_for_status.return_value = None
    mock_httpx_post.return_value = mock_response
    
    client = OllamaClient("http://fake")
    text, tokens = await client.generate("Say hello")
    
    assert text == "Hello"
    assert tokens == 2

@pytest.mark.asyncio
async def test_generate_streaming_error_handling(mock_httpx_stream):
    mock_httpx_stream.raise_for_status.side_effect = Exception("Connection refused")
    
    client = OllamaClient("http://fake")
    
    errors = []
    async for token in client.generate_streaming("prompt"):
        if token.startswith('{"__jarvis_error__"'):
            errors.append(json.loads(token))
            
    assert len(errors) == 1
    assert "Connection refused" in errors[0]["__jarvis_error__"]
