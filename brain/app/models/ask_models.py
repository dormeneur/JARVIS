"""Pydantic models for AI requests and responses."""

from pydantic import BaseModel, Field
from typing import List, Optional


class AskOptions(BaseModel):
    """Configuration options for AI queries."""
    
    top_k: int = Field(default=5, description="Number of chunks to retrieve")
    filter_paths: List[str] = Field(default_factory=list, description="Path prefixes to filter")
    include_sources: bool = Field(default=True, description="Include source attribution")
    stream: bool = Field(default=True, description="Stream response tokens")


class AskRequest(BaseModel):
    """Request model for AI queries."""
    
    query: str = Field(..., description="Natural language query")
    attachments: List[str] = Field(default_factory=list, description="File paths to include as context")
    options: Optional[AskOptions] = Field(default_factory=AskOptions)


class Source(BaseModel):
    """Source attribution for retrieved context."""
    
    path: str = Field(..., description="Vault file path")
    chunk: int = Field(..., description="Chunk index within file")
    score: float = Field(..., description="Similarity score (0-1)")
    content: Optional[str] = Field(default=None, exclude=True, description="Chunk text (internal use only, excluded from API response)")


class AskResponse(BaseModel):
    """Response model for AI queries."""
    
    answer: str = Field(..., description="Generated answer")
    sources: List[Source] = Field(default_factory=list, description="Source attributions")
    model: str = Field(..., description="LLM model used")
    tokens_used: int = Field(..., description="Total tokens in response")


class IndexStatus(BaseModel):
    """Status of vector index."""
    
    total_files_indexed: int
    total_chunks: int
    last_index_run: str  # ISO 8601 timestamp
    pending_files: int
    index_health: str  # "healthy", "indexing", "error"
