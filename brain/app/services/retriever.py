"""Retriever service for semantic search over vector store."""

import logging
from typing import List, Dict, Any, Optional

from app.config import settings
from app.models.ask_models import Source
from app.services.embedding_pipeline import EmbeddingPipeline
from app.services.vector_store import VectorStore

logger = logging.getLogger(__name__)

# Constants
MIN_SIMILARITY_SCORE = 0.3
DEFAULT_TOP_K = 5
RRF_K = 60
MAX_CHUNKS_PER_FILE = 2
OVERFETCH = 4  # candidates pulled from each side before fusing


def rrf_fuse(rankings: List[List[str]], k: int = RRF_K) -> List[tuple]:
    """Reciprocal Rank Fusion: score(id) = sum over lists of 1 / (k + rank), rank starting at 1.

    Robust to the two signals having very different score scales (BM25 vs cosine).
    Returns [(id, score)], best first; ties keep the order they were first seen in.
    """
    scores: Dict[str, float] = {}
    for ranking in rankings:
        for rank, item in enumerate(ranking, start=1):
            scores[item] = scores.get(item, 0.0) + 1.0 / (k + rank)
    return sorted(scores.items(), key=lambda kv: kv[1], reverse=True)


def cap_per_file(items: List[Dict[str, Any]], n: int = MAX_CHUNKS_PER_FILE) -> List[Dict[str, Any]]:
    """Keep at most n items per file, preserving order."""
    seen: Dict[str, int] = {}
    kept = []
    for item in items:
        count = seen.get(item["path"], 0)
        if count < n:
            kept.append(item)
            seen[item["path"]] = count + 1
    return kept

class Retriever:
    """Service to retrieve relevant context chunks for a query."""
    
    def __init__(
        self,
        embedding_pipeline: EmbeddingPipeline,
        vector_store: VectorStore,
        keyword_index: Any = None,
    ):
        self.embedder = embedding_pipeline
        self.store = vector_store
        self.keyword = keyword_index

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
        if self.keyword is not None and settings.hybrid_search:
            return await self._retrieve_hybrid(query, top_k, filter_paths)

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

    async def _retrieve_hybrid(
        self, query: str, top_k: int, filter_paths: Optional[List[str]]
    ) -> List[Source]:
        """Vector + BM25, fused with RRF, at most two chunks per file."""
        query_embedding = await self.embedder.embed_query(query)
        n = top_k * OVERFETCH
        raw = await self.store.query(query_embedding=query_embedding, top_k=n, filter_paths=filter_paths)
        keyword_rows = await self.keyword.search(query, limit=n, filter_paths=filter_paths)

        chunks: Dict[str, Dict[str, Any]] = {}
        vector_ids: List[str] = []
        if raw and raw.get("distances") and raw["distances"][0]:
            distances = raw["distances"][0]
            ids = (raw.get("ids") or [[]])[0]
            metas = (raw.get("metadatas") or [[]])[0]
            docs = (raw.get("documents") or [[]])[0]
            for i, distance in enumerate(distances):
                if 1.0 - distance < MIN_SIMILARITY_SCORE:
                    continue
                meta = metas[i] if i < len(metas) else {}
                path = meta.get("source_path", "unknown")
                index = meta.get("chunk_index", 0)
                chunk_id = ids[i] if i < len(ids) else f"{path}|{index}"
                chunks[chunk_id] = {"path": path, "chunk": index, "content": docs[i] if i < len(docs) else ""}
                vector_ids.append(chunk_id)

        keyword_ids: List[str] = []
        for row in keyword_rows:
            chunks.setdefault(
                row["chunk_id"], {"path": row["path"], "chunk": row["chunk_index"], "content": row["content"]}
            )
            keyword_ids.append(row["chunk_id"])

        fused = rrf_fuse([vector_ids, keyword_ids])
        ordered = [dict(chunks[cid], score=score) for cid, score in fused]
        top = cap_per_file(ordered)[:top_k]
        best = top[0]["score"] if top else 1.0
        # Normalised to 0..1 (the API contract for Source.score): 1.0 = the best match for this query.
        return [Source(path=c["path"], chunk=c["chunk"], score=c["score"] / best, content=c["content"]) for c in top]

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
