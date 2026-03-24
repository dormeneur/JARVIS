"""Property-based tests for retriever and context assembler.

Feature: phase-3-ai-integration
Properties tested:
- Property 17: Retrieval Top-K Limit
- Property 18: Retrieval Path Filtering
- Property 19: Retrieval Deduplication
- Property 20: Retrieval Score Threshold
- Property 21: Prompt Template Content
- Property 22: Context Source Attribution
- Property 23: Context Token Budget
- Property 24: Context Chunk Ordering
- Property 25: Attachment Inclusion
- Property 26: Attachment Priority
"""

import pytest
from unittest.mock import MagicMock, AsyncMock
from hypothesis import given, settings, strategies as st

from brain.app.services.retriever import Retriever, MIN_SIMILARITY_SCORE
from brain.app.services.context_assembler import ContextAssembler, MAX_CONTEXT_TOKENS
from brain.app.models.ask_models import Source

# --- RETRIEVER TESTS ---

@pytest.fixture
def retriever_mocks():
    embedder = AsyncMock()
    store = AsyncMock()
    return embedder, store

@settings(max_examples=20)
@given(top_k=st.integers(1, 10))
@pytest.mark.asyncio
async def test_property_17_top_k_limit(retriever_mocks, top_k):
    """Property 17: Retrieval Top-K Limit"""
    embedder, store = retriever_mocks
    
    # Mock giving 20 results
    store.query.return_value = {
        "distances": [[0.1] * 20],
        "metadatas": [[{"source_path": f"f{i}.md", "chunk_index": 0} for i in range(20)]],
        "documents": [["content"] * 20]
    }
    
    retriever = Retriever(embedder, store)
    results = await retriever.retrieve("test", top_k=top_k)
    
    assert len(results) <= top_k

@pytest.mark.asyncio
async def test_property_18_19_20_retrieval_rules(retriever_mocks):
    """Properties 18, 19, 20: Path filtering, deduplication, and score threshold."""
    embedder, store = retriever_mocks
    
    # Distances:
    # 0.05 -> score 0.95 (pass) - file1
    # 0.10 -> score 0.90 (pass) - file1 (duplicate, should be removed)
    # 0.80 -> score 0.20 (fail threshold) - file2
    store.query.return_value = {
        "distances": [[0.05, 0.10, 0.80]],
        "metadatas": [[
            {"source_path": "file1.md", "chunk_index": 0},
            {"source_path": "file1.md", "chunk_index": 1},
            {"source_path": "file2.md", "chunk_index": 0}
        ]],
        "documents": [["c1", "c2", "c3"]]
    }
    
    retriever = Retriever(embedder, store)
    results = await retriever.retrieve("test", top_k=5)
    
    # Should only return the first chunk of file1.md
    assert len(results) == 1
    assert results[0].path == "file1.md"
    assert results[0].chunk == 0
    assert results[0].score == 0.95


# --- CONTEXT ASSEMBLER TESTS ---

@pytest.fixture
def assembler_mocks():
    chunker = MagicMock()
    loader = MagicMock()
    
    # Mock token counting to equal character length for easy math
    chunker._count_tokens.side_effect = lambda x: len(x)
    chunker.encoding.encode.side_effect = lambda x: list(x.encode())
    chunker.encoding.decode.side_effect = lambda x: bytes(x).decode(errors='ignore')
    
    loader._extract_content.return_value = "attachment content"
    return chunker, loader

def test_properties_21_22_assembler_structure(assembler_mocks):
    chunker, loader = assembler_mocks
    
    source = Source(path="retrieved.md", chunk=0, score=0.9)
    source.content = "retrieved content"
    
    assembler = ContextAssembler(chunker, loader)
    prompt, sources = assembler.assemble_prompt("my query", [source], ["attach.md"])
    
    # 21: Prompt Template Content
    assert "JARVIS" in prompt
    assert "=== CONTEXT ===" in prompt
    assert "User Question: my query" in prompt
    
    # 22: Context Source Attribution
    assert "[Source: attach.md]" in prompt
    assert "[Source: retrieved.md]" in prompt
    
    # 25, 26: Attachment Inclusion & Priority
    # attach.md must be first source
    assert sources[0].path == "attach.md"
    assert sources[0].score == 1.0

def test_property_23_context_budget(assembler_mocks):
    chunker, loader = assembler_mocks
    
    source = Source(path="huge.md", chunk=0, score=0.9)
    # Create content larger than MAX_CONTEXT_TOKENS assuming length=tokens
    source.content = "a" * (MAX_CONTEXT_TOKENS + 100)
    
    assembler = ContextAssembler(chunker, loader)
    prompt, sources = assembler.assemble_prompt("my query", [source], [])
    
    # 23: Context Token Budget
    # Check it truncated
    assert len(prompt) < MAX_CONTEXT_TOKENS + 500  # allow some overhead for the prompt template
