"""Integration tests for POST /brain/ask."""

import pytest
import json
from unittest.mock import AsyncMock, patch
from fastapi import Request

from brain.app.routers.ask import ask_brain
from brain.app.models.ask_models import AskRequest, AskOptions

@pytest.fixture
def mock_request():
    request = AsyncMock(spec=Request)
    request.app = AsyncMock()
    request.app.state = AsyncMock()
    request.app.state.embedding_pipeline.ollama_url = "http://fake"
    return request

@pytest.fixture
def patch_services():
    with patch("brain.app.routers.ask.Retriever") as mock_ret, \
         patch("brain.app.routers.ask.ContextAssembler") as mock_asm, \
         patch("brain.app.routers.ask.OllamaClient") as mock_ollama:
        
        mock_ret_instance = AsyncMock()
        mock_ret_instance.retrieve.return_value = []
        mock_ret.return_value = mock_ret_instance
        
        mock_asm_instance = AsyncMock()
        mock_asm_instance.assemble_prompt.return_value = ("prompt", [])
        mock_asm.return_value = mock_asm_instance
        
        mock_ollama_instance = AsyncMock()
        
        async def mock_stream(prompt):
            yield "Hello "
            yield "world."
            yield json.dumps({"__jarvis_metadata__": True, "eval_count": 2})
            
        mock_ollama_instance.generate_streaming = mock_stream
        mock_ollama.return_value = mock_ollama_instance
        
        yield

@pytest.mark.asyncio
async def test_ask_non_streaming(mock_request, patch_services):
    req = AskRequest(query="test", options=AskOptions(stream=False))
    
    response = await ask_brain(req, mock_request)
    
    # Should be AskResponse model directly since stream=False
    assert response.answer == "Hello world."
    assert response.tokens_used == 2

@pytest.mark.asyncio
async def test_ask_streaming_response_type(mock_request, patch_services):
    from fastapi.responses import StreamingResponse
    req = AskRequest(query="test", options=AskOptions(stream=True))
    
    response = await ask_brain(req, mock_request)
    
    assert isinstance(response, StreamingResponse)
    assert response.media_type == "application/x-ndjson"
