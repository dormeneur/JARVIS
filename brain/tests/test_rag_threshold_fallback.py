"""Dedicated test: ChromaDB threshold fallback to base Ollama generation.

When no chunks score above MIN_SIMILARITY_SCORE, the pipeline must still
produce a valid response via base Ollama generation (no retrieved context),
rather than raising an exception or returning empty.
"""

import json
import pytest
from unittest.mock import MagicMock, AsyncMock, patch

from brain.app.routers.ask import generate_rag_stream
from brain.app.models.ask_models import AskRequest, AskOptions


@pytest.mark.asyncio
async def test_rag_threshold_fallback_to_base_generation():
    """When vector store returns zero matches, we still get a valid streamed answer."""

    # Mock App State
    app_state = MagicMock()
    app_state.embedding_pipeline = MagicMock()
    app_state.embedding_pipeline.ollama_url = "http://localhost:11434"
    app_state.embedding_pipeline.embed_query = AsyncMock(return_value=[0.1] * 768)
    app_state.vector_store = MagicMock()
    app_state.text_chunker = MagicMock()
    app_state.text_chunker._count_tokens = MagicMock(side_effect=lambda x: len(x))
    app_state.text_chunker.encoding = MagicMock()
    app_state.text_chunker.encoding.encode = MagicMock(side_effect=lambda x: list(x.encode()))
    app_state.text_chunker.encoding.decode = MagicMock(side_effect=lambda x: bytes(x).decode(errors="ignore"))
    app_state.document_loader = MagicMock()
    app_state.document_loader._extract_content = MagicMock(return_value=None)

    # VectorStore returns empty → nothing above threshold
    app_state.vector_store.query = AsyncMock(
        return_value={"distances": [], "metadatas": [], "documents": []}
    )

    # Mock the Ollama streaming response
    async def _fake_aiter_lines():
        yield json.dumps({"response": "42", "done": False})
        yield json.dumps({"response": "", "done": True, "eval_count": 10})

    mock_resp = AsyncMock()
    mock_resp.raise_for_status = MagicMock()
    mock_resp.aiter_lines = _fake_aiter_lines

    mock_ctx = AsyncMock()
    mock_ctx.__aenter__ = AsyncMock(return_value=mock_resp)
    mock_ctx.__aexit__ = AsyncMock(return_value=False)

    request = AskRequest(
        query="What is the meaning of life?",
        options=AskOptions(top_k=5, stream=True),
    )

    with patch("brain.app.services.context_assembler.settings") as mock_settings, \
         patch("httpx.AsyncClient.stream", return_value=mock_ctx) as mock_stream:
        mock_settings.vault_path = "/nonexistent"

        chunks = []
        async for chunk in generate_rag_stream(request, app_state):
            chunks.append(chunk)

    assert len(chunks) > 0, "Should produce at least one NDJSON line"

    # Verify the prompt that was sent to Ollama contained the fallback marker
    call_args = mock_stream.call_args
    prompt_sent = call_args[1]["json"]["prompt"] if "json" in call_args[1] else call_args[0][2]["prompt"]
    assert "No relevant context found." in prompt_sent, (
        "When no chunks pass threshold, prompt must contain 'No relevant context found.'"
    )

    # Final line must be a valid AskResponse
    final = json.loads(chunks[-1])
    assert "answer" in final
    assert "42" in final["answer"]
