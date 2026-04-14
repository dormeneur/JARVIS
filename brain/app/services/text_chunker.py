"""Text chunking service with token-based splitting."""

import hashlib
from dataclasses import dataclass
from typing import List

import tiktoken
from langchain_text_splitters import RecursiveCharacterTextSplitter

from app.services.document_loader import LoadedDocument


# Configuration constants
CHUNK_SIZE_TOKENS = 512
CHUNK_OVERLAP_TOKENS = 64
SPLIT_SEQUENCE = ["\n\n", "\n", ". ", " "]
ENCODING = "cl100k_base"


@dataclass
class Chunk:
    """Represents a text chunk with metadata."""
    chunk_id: str
    source_path: str
    chunk_index: int
    total_chunks: int
    content: str
    content_hash: str


class TextChunker:
    """Splits documents into token-sized chunks with overlap."""
    
    def __init__(self):
        """Initialize the text chunker with tiktoken encoding."""
        self.encoding = tiktoken.get_encoding(ENCODING)
        self.splitter = RecursiveCharacterTextSplitter(
            chunk_size=CHUNK_SIZE_TOKENS,
            chunk_overlap=CHUNK_OVERLAP_TOKENS,
            length_function=self._count_tokens,
            separators=SPLIT_SEQUENCE
        )
    
    def _count_tokens(self, text: str) -> int:
        """Count tokens using tiktoken.
        
        Args:
            text: Text to count tokens for
            
        Returns:
            Number of tokens in the text
        """
        return len(self.encoding.encode(text))
    
    def chunk_document(self, doc: LoadedDocument) -> List[Chunk]:
        """Split document into chunks.
        
        Args:
            doc: LoadedDocument to chunk
            
        Returns:
            List of Chunk objects with metadata
        """
        texts = self.splitter.split_text(doc.content)
        chunks = []
        
        for i, text in enumerate(texts):
            chunk_id = self._generate_chunk_id(doc.path, i)
            content_hash = hashlib.sha256(text.encode()).hexdigest()
            
            chunks.append(Chunk(
                chunk_id=chunk_id,
                source_path=doc.path,
                chunk_index=i,
                total_chunks=len(texts),
                content=text,
                content_hash=content_hash
            ))
        
        return chunks
    
    def _generate_chunk_id(self, source_path: str, chunk_index: int) -> str:
        """Generate deterministic chunk ID.
        
        Args:
            source_path: Path to source document
            chunk_index: Index of chunk within document
            
        Returns:
            SHA-256 hash of source_path + "|" + chunk_index
        """
        key = f"{source_path}|{chunk_index}"
        return hashlib.sha256(key.encode()).hexdigest()
