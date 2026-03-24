"""Property-based tests for /brain/ask endpoint."""

import pytest
import json
from unittest.mock import AsyncMock, patch
from hypothesis import given, settings, strategies as st

from brain.app.routers.ask import generate_rag_stream
from brain.app.models.ask_models import AskRequest, AskOptions

@pytest.fixture
def mock_app_state():
    """Mock application state with mocked services."""
    class State:
        embedding_pipeline = AsyncMock()
        vector_store = AsyncMock()
        text_chunker = AsyncMock()
        document_loader = AsyncMock()
        
    state = State()
    state.embedding_pipeline.ollama_url = "http://fake"
    return state

@pytest.fixture
def mock_retriever_assembler_ollama():
    """Patch the service classes used in the router."""
    with patch("brain.app.routers.ask.Retriever") as mock_ret, \
         patch("brain.app.routers.ask.ContextAssembler") as mock_asm, \
         patch("brain.app.routers.ask.OllamaClient") as mock_ollama:
        
        # Setup mocks
        mock_ret_instance = AsyncMock()
        mock_ret_instance.retrieve.return_value = []
        mock_ret.return_value = mock_ret_instance
        
        mock_asm_instance = AsyncMock()
        mock_asm_instance.assemble_prompt.return_value = ("prompt", [])
        mock_asm.return_value = mock_asm_instance
        
        mock_ollama_instance = AsyncMock()
        mock_ollama.return_value = mock_ollama_instance
        
        yield mock_ollama_instance

@settings(max_examples=20)
@given(tokens=st.lists(st.text(min_size=1, max_size=10), min_size=1, max_size=5))
@pytest.mark.asyncio
async def test_property_29_streaming_response_format(mock_app_state, mock_retriever_assembler_ollama, tokens):
    """Property 29: Streaming Response Format
    
    **Validates: Requirement 10.7**
    Verify response is NDJSON format with streaming tokens.
    """
    async def mock_generate_streaming(prompt):
        for t in tokens:
            yield t
        yield json.dumps({"__jarvis_metadata__": True, "eval_count": 42})
        
    mock_retriever_assembler_ollama.generate_streaming = mock_generate_streaming
    
    req = AskRequest(query="test", options=AskOptions(stream=True))
    
    stream_results = []
    async for chunk in generate_rag_stream(req, mock_app_state):
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
async def test_property_30_complete_response_structure(mock_app_state, mock_retriever_assembler_ollama, query):
    """Property 30: Complete Response Structure
    
    **Validates: Requirement 10.8**
    Verify final AskResponse includes answer, sources, model, tokens_used.
    """
    async def mock_generate_streaming(prompt):
        yield "FinalAnswer"
        yield json.dumps({"__jarvis_metadata__": True, "eval_count": 99})
        
    mock_retriever_assembler_ollama.generate_streaming = mock_generate_streaming
    
    req = AskRequest(query=query)
    stream_results = []
    async for chunk in generate_rag_stream(req, mock_app_state):
        stream_results.append(chunk)
        
    final_dict = json.loads(stream_results[-1])
    
    assert "answer" in final_dict
    assert final_dict["answer"] == "FinalAnswer"
    assert "sources" in final_dict
    assert "model" in final_dict
    assert "tokens_used" in final_dict
    assert final_dict["tokens_used"] == 99
