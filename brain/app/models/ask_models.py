"""Pydantic models for AI requests and responses."""

from pydantic import BaseModel, Field
from typing import List, Optional


class AskOptions(BaseModel):
    """Configuration options for AI queries."""
    
    top_k: int = Field(default=5, description="Number of chunks to retrieve")
    filter_paths: List[str] = Field(default_factory=list, description="Path prefixes to filter")
    include_sources: bool = Field(default=True, description="Include source attribution")
    stream: bool = Field(default=True, description="Stream response tokens")


class Message(BaseModel):
    role: str
    content: str


class AskRequest(BaseModel):
    """Request model for AI queries."""
    
    query: str = Field(..., description="Natural language query")
    current_directory: str = Field(default=".", description="Context directory relative to vault")
    chat_history: List[Message] = Field(default_factory=list, description="Chat turns")
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
    last_index_run: Optional[str] = None  # ISO 8601 timestamp
    pending_files: int
    index_health: str  # "healthy", "indexing", "error"


class ExtractPdfRequest(BaseModel):
    """Request model for extracting text from specific PDF pages."""
    
    path: str = Field(..., description="Vault-relative path to the PDF file")
    start_page: int = Field(default=1, description="1-indexed start page")
    end_page: Optional[int] = Field(default=None, description="1-indexed end page (inclusive). If None, extracts to the end.")


class ExtractPdfResponse(BaseModel):
    """Response model for PDF text extraction."""
    
    markdown: str = Field(..., description="Extracted text formatted as Markdown")
    pages_extracted: int = Field(..., description="Number of pages successfully extracted")
    total_pages: int = Field(..., description="Total pages in the document")


class GenerateFileManifestItem(BaseModel):
    """A single file or folder to be created by the AI."""
    
    path: str = Field(..., description="Relative path including filename")
    content: str = Field(default="", description="Text content for the file")
    type: str = Field(default="file", description="'file' or 'directory'")


class GenerateFilesRequest(BaseModel):
    """Request model for natural language file generation."""
    
    prompt: str = Field(..., description="Natural language request to create files")
    current_directory: str = Field(default=".", description="Context directory where files should be created")
