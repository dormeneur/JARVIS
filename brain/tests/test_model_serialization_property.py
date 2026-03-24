"""
Property-based tests for Pydantic model serialization.

Feature: phase-3-ai-integration
Property 1: Model Serialization Round-Trip
Validates: Requirements 1.1, 1.3, 1.4, 1.5
"""

import sys
from pathlib import Path

# Add parent directory to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

import pytest
from hypothesis import given, settings, strategies as st
from app.models.ask_models import (
    AskRequest,
    AskResponse,
    AskOptions,
    Source,
    IndexStatus,
)


# Hypothesis strategies for generating test data
@st.composite
def ask_options_strategy(draw):
    """Generate valid AskOptions instances."""
    return AskOptions(
        top_k=draw(st.integers(min_value=1, max_value=100)),
        filter_paths=draw(st.lists(st.text(min_size=1, max_size=50), max_size=10)),
        include_sources=draw(st.booleans()),
        stream=draw(st.booleans()),
    )


@st.composite
def ask_request_strategy(draw):
    """Generate valid AskRequest instances."""
    return AskRequest(
        query=draw(st.text(min_size=1, max_size=500)),
        attachments=draw(st.lists(st.text(min_size=1, max_size=100), max_size=10)),
        options=draw(st.one_of(st.none(), ask_options_strategy())),
    )


@st.composite
def source_strategy(draw):
    """Generate valid Source instances."""
    return Source(
        path=draw(st.text(min_size=1, max_size=200)),
        chunk=draw(st.integers(min_value=0, max_value=1000)),
        score=draw(st.floats(min_value=0.0, max_value=1.0, allow_nan=False, allow_infinity=False)),
    )


@st.composite
def ask_response_strategy(draw):
    """Generate valid AskResponse instances."""
    return AskResponse(
        answer=draw(st.text(min_size=0, max_size=5000)),
        sources=draw(st.lists(source_strategy(), max_size=20)),
        model=draw(st.text(min_size=1, max_size=50)),
        tokens_used=draw(st.integers(min_value=0, max_value=100000)),
    )


@st.composite
def index_status_strategy(draw):
    """Generate valid IndexStatus instances."""
    return IndexStatus(
        total_files_indexed=draw(st.integers(min_value=0, max_value=100000)),
        total_chunks=draw(st.integers(min_value=0, max_value=1000000)),
        last_index_run=draw(st.text(min_size=1, max_size=50)),  # ISO 8601 timestamp
        pending_files=draw(st.integers(min_value=0, max_value=10000)),
        index_health=draw(st.sampled_from(["healthy", "indexing", "error"])),
    )


# Property tests
@pytest.mark.property
@given(ask_options_strategy())
@settings(max_examples=100)
def test_ask_options_serialization_roundtrip(options):
    """
    Property 1: Model Serialization Round-Trip
    
    For any AskOptions instance, serializing to JSON and deserializing back
    should produce an equivalent object with all fields preserved.
    
    **Validates: Requirements 1.1, 1.3, 1.4, 1.5**
    """
    # Serialize to JSON
    json_str = options.model_dump_json()
    
    # Deserialize back
    restored = AskOptions.model_validate_json(json_str)
    
    # Verify equivalence
    assert restored == options
    assert restored.top_k == options.top_k
    assert restored.filter_paths == options.filter_paths
    assert restored.include_sources == options.include_sources
    assert restored.stream == options.stream


@pytest.mark.property
@given(ask_request_strategy())
@settings(max_examples=100)
def test_ask_request_serialization_roundtrip(request):
    """
    Property 1: Model Serialization Round-Trip
    
    For any AskRequest instance, serializing to JSON and deserializing back
    should produce an equivalent object with all fields preserved.
    
    **Validates: Requirements 1.1, 1.3, 1.4, 1.5**
    """
    # Serialize to JSON
    json_str = request.model_dump_json()
    
    # Deserialize back
    restored = AskRequest.model_validate_json(json_str)
    
    # Verify equivalence
    assert restored == request
    assert restored.query == request.query
    assert restored.attachments == request.attachments
    if request.options is not None:
        assert restored.options == request.options


@pytest.mark.property
@given(source_strategy())
@settings(max_examples=100)
def test_source_serialization_roundtrip(source):
    """
    Property 1: Model Serialization Round-Trip
    
    For any Source instance, serializing to JSON and deserializing back
    should produce an equivalent object with all fields preserved.
    
    **Validates: Requirements 1.1, 1.3, 1.4, 1.5**
    """
    # Serialize to JSON
    json_str = source.model_dump_json()
    
    # Deserialize back
    restored = Source.model_validate_json(json_str)
    
    # Verify equivalence
    assert restored == source
    assert restored.path == source.path
    assert restored.chunk == source.chunk
    assert abs(restored.score - source.score) < 1e-9  # Float comparison with tolerance


@pytest.mark.property
@given(ask_response_strategy())
@settings(max_examples=100)
def test_ask_response_serialization_roundtrip(response):
    """
    Property 1: Model Serialization Round-Trip
    
    For any AskResponse instance, serializing to JSON and deserializing back
    should produce an equivalent object with all fields preserved.
    
    **Validates: Requirements 1.1, 1.3, 1.4, 1.5**
    """
    # Serialize to JSON
    json_str = response.model_dump_json()
    
    # Deserialize back
    restored = AskResponse.model_validate_json(json_str)
    
    # Verify equivalence
    assert restored == response
    assert restored.answer == response.answer
    assert len(restored.sources) == len(response.sources)
    assert restored.model == response.model
    assert restored.tokens_used == response.tokens_used


@pytest.mark.property
@given(index_status_strategy())
@settings(max_examples=100)
def test_index_status_serialization_roundtrip(status):
    """
    Property 1: Model Serialization Round-Trip
    
    For any IndexStatus instance, serializing to JSON and deserializing back
    should produce an equivalent object with all fields preserved.
    
    **Validates: Requirements 1.1, 1.3, 1.4, 1.5**
    """
    # Serialize to JSON
    json_str = status.model_dump_json()
    
    # Deserialize back
    restored = IndexStatus.model_validate_json(json_str)
    
    # Verify equivalence
    assert restored == status
    assert restored.total_files_indexed == status.total_files_indexed
    assert restored.total_chunks == status.total_chunks
    assert restored.last_index_run == status.last_index_run
    assert restored.pending_files == status.pending_files
    assert restored.index_health == status.index_health
