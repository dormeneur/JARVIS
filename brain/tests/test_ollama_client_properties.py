"""Property-based tests for Ollama client.

Feature: phase-3-ai-integration
Properties tested:
- Property 27: NDJSON Parsing
- Property 28: Token Count Extraction
"""

import json
import pytest
from unittest.mock import AsyncMock, patch, MagicMock
from hypothesis import given, settings, strategies as st

from brain.app.services.ollama_client import OllamaClient

@pytest.fixture
def mock_httpx_stream():
    """Mock httpx.AsyncClient.stream"""
    with patch("httpx.AsyncClient.stream") as mock_stream:
        # Complex mock because stream is an async context manager
        mock_ctx = AsyncMock()
        mock_response = AsyncMock()
        mock_ctx.__aenter__.return_value = mock_response
        mock_stream.return_value = mock_ctx
        yield mock_response

@settings(max_examples=50)
@given(responses=st.lists(st.text(min_size=1, max_size=20), min_size=1, max_size=10))
@pytest.mark.asyncio
async def test_property_27_ndjson_parsing(mock_httpx_stream, responses):
    """Property 27: NDJSON Parsing"""
    
    # Create valid NDJSON stream mock
    async def mock_aiter_lines():
        for r in responses:
            yield json.dumps({"response": r, "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 42})
        
    mock_httpx_stream.aiter_lines = mock_aiter_lines
    
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
async def test_property_28_token_count(mock_httpx_stream, eval_count):
    """Property 28: Token Count extraction"""
    
    async def mock_aiter_lines():
        yield json.dumps({"response": "done", "done": True, "eval_count": eval_count})
        
    mock_httpx_stream.aiter_lines = mock_aiter_lines
    
    client = OllamaClient("http://fake")
    metadata = None
    
    async for token in client.generate_streaming("prompt"):
        if token.startswith('{"__jarvis_metadata__"'):
            metadata = json.loads(token)
            
    assert metadata is not None
    assert metadata["eval_count"] == eval_count
