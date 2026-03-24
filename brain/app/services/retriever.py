"""Retriever service for semantic search over vector store."""

import logging
from typing import List, Dict, Any, Optional

from app.models.ask_models import Source
from app.services.embedding_pipeline import EmbeddingPipeline
from app.services.vector_store import VectorStore

logger = logging.getLogger(__name__)

# Constants
MIN_SIMILARITY_SCORE = 0.3
DEFAULT_TOP_K = 5

class Retriever:
    """Service to retrieve relevant context chunks for a query."""
    
    def __init__(
        self,
        embedding_pipeline: EmbeddingPipeline,
        vector_store: VectorStore
    ):
        self.embedder = embedding_pipeline
        self.store = vector_store

    async def retrieve(
        self, 
        query: str, 
        top_k: int = DEFAULT_TOP_K, 
        filter_paths: Optional[List[str]] = None
    ) -> List[Source]:
        """Retrieve relevant context chunks for a query.
        
        Args:
            query: The user's semantic search query
            top_k: Max number of results to retrieve (before deduplication)
            filter_paths: Optional list of specific paths to search within
            
        Returns:
            List of Source objects containing path, chunk_index, and score
        """
        # Embed the query
        query_embedding = await self.embedder.embed_query(query)
        
        # Query vector store
        # Ask for 2*top_k initially to allow room for deduplication
        raw_results = await self.store.query(
            query_embedding=query_embedding,
            top_k=top_k * 2,
            filter_paths=filter_paths
        )
        
        if not raw_results or "distances" not in raw_results or not raw_results["distances"]:
            return []
            
        # Parse ChromaDB results
        # distances[0] are the distances for the first (and only) query vector
        # ChromaDB distance is cosine distance (0 means perfectly similar, 1 means orthogonal)
        # Cosine similarity = 1 - cosine distance
        distances = raw_results["distances"][0]
        metadatas = raw_results["metadatas"][0] if "metadatas" in raw_results else []
        documents = raw_results["documents"][0] if "documents" in raw_results else []
        
        parsed_results = []
        for i in range(len(distances)):
            meta = metadatas[i] if metadatas and i < len(metadatas) else {}
            doc = documents[i] if documents and i < len(documents) else ""
            
            # Convert ChromaDB distance back to similarity score
            score = 1.0 - distances[i]
            
            # Apply threshold
            if score >= MIN_SIMILARITY_SCORE:
                parsed_results.append({
                    "path": meta.get("source_path", "unknown"),
                    "chunk": meta.get("chunk_index", 0),
                    "score": score,
                    "content": doc
                })
                
        # Deduplicate
        deduped = self._deduplicate_by_source(parsed_results)
        
        # Sort by score descending and take top_k
        deduped.sort(key=lambda x: x["score"], reverse=True)
        final_results = deduped[:top_k]
        
        # Map to Source model (Source only contains metadata for API response, not full payload)
        # Actually in our internal pipeline we'll need the content to assemble the prompt.
        # We'll return full Source objects extended with 'content' for internal use.
        sources = []
        for res in final_results:
            source = Source(
                path=res["path"],
                chunk=res["chunk"],
                score=res["score"],
                content=res["content"]
            )
            sources.append(source)
            
        return sources

    def _deduplicate_by_source(self, results: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
        """Keep only the highest-scoring chunk per file.
        
        Args:
            results: List of result dictionaries
            
        Returns:
            Deduplicated list
        """
        best_by_path = {}
        
        for res in results:
            path = res["path"]
            # Since we iterate, if we haven't seen this path, or if this score is better:
            if path not in best_by_path or res["score"] > best_by_path[path]["score"]:
                best_by_path[path] = res
                
        return list(best_by_path.values())
