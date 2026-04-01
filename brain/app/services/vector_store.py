"""Vector storage service using ChromaDB."""

import logging
from typing import List, Dict, Any, Optional

import chromadb
from chromadb.config import Settings

from app.services.text_chunker import Chunk

logger = logging.getLogger(__name__)

# Constants
COLLECTION_NAME = "jarvis_vault"
EMBEDDING_DIMENSION = 768

class VectorStore:
    """Service to interact with ChromaDB vector store."""
    
    def __init__(self, vectordb_url: str):
        """Initialize connection to ChromaDB.
        
        Args:
            vectordb_url: URL to ChromaDB container (e.g., http://chromadb:8000)
        """
        # Parse host and port from URL (assuming http://host:port format)
        host = vectordb_url.replace("http://", "").replace("https://", "").split(":")[0]
        port = int(vectordb_url.split(":")[-1]) if ":" in vectordb_url.replace("http://", "").replace("https://", "") else 8000
        
        # Use HttpClient which does not require compilation of local C++ extensions
        self.client = chromadb.HttpClient(
            host=host,
            port=port
        )
        
        # Create or get the collection with cosine distance metric
        self.collection = self.client.get_or_create_collection(
            name=COLLECTION_NAME,
            metadata={"hnsw:space": "cosine"}
        )
        logger.info(f"Connected to ChromaDB at {host}:{port}, collection '{COLLECTION_NAME}'")

    async def upsert_chunks(self, chunks: List[Chunk], embeddings: List[List[float]], last_modified: str, summary: str = "") -> None:
        """Insert or update chunks with their embeddings.
        
        Args:
            chunks: List of Chunk objects
            embeddings: List of 768-dim float vectors corresponding to chunks
            last_modified: ISO 8601 timestamp string of the source file
        """
        if not chunks or not embeddings:
            return
            
        if len(chunks) != len(embeddings):
            raise ValueError(f"Mismatched chunks ({len(chunks)}) and embeddings ({len(embeddings)})")

        ids = [c.chunk_id for c in chunks]
        documents = [c.content for c in chunks]
        metadatas = [
            {
                "source_path": c.source_path,
                "chunk_index": c.chunk_index,
                "content_hash": c.content_hash,
                "last_modified": last_modified,
                "summary": summary
            }
            for c in chunks
        ]
        
        # Upsert operation (inserts if ID new, updates if ID exists)
        self.collection.upsert(
            ids=ids,
            embeddings=embeddings,
            metadatas=metadatas,
            documents=documents
        )
        logger.debug(f"Upserted {len(chunks)} chunks to ChromaDB")

    async def delete_by_path(self, source_path: str) -> None:
        """Remove all chunks associated with a specific file path.
        
        Args:
            source_path: The vault-relative path to the file
        """
        # Delete using metadata filtering
        self.collection.delete(
            where={"source_path": source_path}
        )
        logger.debug(f"Deleted chunks for path: {source_path}")

    async def query(self, query_embedding: List[float], top_k: int = 5, filter_paths: Optional[List[str]] = None) -> Dict[str, Any]:
        """Search the vector store for similar chunks.
        
        Args:
            query_embedding: The 768-dim float vector of the search query
            top_k: Number of maximum results to return
            filter_paths: Optional list of paths to restrict the search to
            
        Returns:
            Dictionary with query results matching ChromaDB's output format
        """
        kwargs = {
            "query_embeddings": [query_embedding],
            "n_results": top_k,
            "include": ["metadatas", "documents", "distances"]
        }
        
        # Add path filtering if provided
        if filter_paths and len(filter_paths) > 0:
            if len(filter_paths) == 1:
                kwargs["where"] = {"source_path": filter_paths[0]}
            else:
                kwargs["where"] = {"source_path": {"$in": filter_paths}}
                
        results = self.collection.query(**kwargs)
        return results

    async def get_all_metadata(self) -> List[Dict[str, Any]]:
        """Retrieve all chunk metadata in the collection for indexing comparison.
        
        Returns:
            List of metadata dictionaries
        """
        # ChromaDB paginates get() by default. We get everything by passing no limit.
        results = self.collection.get(
            include=["metadatas"]
        )
        return results.get("metadatas", [])

    async def count(self) -> int:
        """Get the total number of chunks stored.
        
        Returns:
            Integer count of items in collection
        """
        return self.collection.count()
