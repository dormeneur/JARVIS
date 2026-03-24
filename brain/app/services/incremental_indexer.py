"""Incremental indexer service orchestrating the RAG pipeline."""

import logging
import asyncio
from datetime import datetime, timezone
from typing import Dict, List, Any

from app.models.ask_models import IndexStatus
from app.services.document_loader import DocumentLoader
from app.services.text_chunker import TextChunker
from app.services.embedding_pipeline import EmbeddingPipeline
from app.services.vector_store import VectorStore

logger = logging.getLogger(__name__)

class IncrementalIndexer:
    """Orchestrates loading, chunking, embedding, and storing documents."""
    
    def __init__(
        self,
        document_loader: DocumentLoader,
        text_chunker: TextChunker,
        embedding_pipeline: EmbeddingPipeline,
        vector_store: VectorStore
    ):
        self.loader = document_loader
        self.chunker = text_chunker
        self.embedder = embedding_pipeline
        self.store = vector_store
        
        # Tracking variables
        self.is_indexing = False
        self.last_index_time = None
        self.files_indexed = 0
        self.chunks_created = 0
        self.files_skipped = 0
        self.files_deleted = 0
        self.files_modified = 0
        self.total_files = 0
        
        self.health = "healthy"
        self._indexing_task = None
        
    def get_status(self) -> IndexStatus:
        """Get the current status of the indexer."""
        return IndexStatus(
            total_files_indexed=self.files_indexed,
            total_chunks=self.chunks_created,
            last_index_run=self.last_index_time.isoformat() if self.last_index_time else None,
            pending_files=max(0, self.total_files - (self.files_indexed + self.files_skipped)),
            index_health="indexing" if self.is_indexing else self.health
        )
        
    def start_background_indexing(self) -> None:
        """Start indexing in the background without blocking."""
        if self.is_indexing:
            logger.warning("Indexing already in progress.")
            return
            
        self._indexing_task = asyncio.create_task(self.run_indexing())
        
    async def run_indexing(self) -> None:
        """Run a full incremental indexing pass over the vault."""
        if self.is_indexing:
            return
            
        self.is_indexing = True
        self.health = "indexing"
        logger.info("Starting incremental indexing pass...")
        
        try:
            # Get current DB state
            existing_metadata = await self.store.get_all_metadata()
            db_state = self._group_by_path(existing_metadata)
            
            # Reset stats for this run
            pass_new = 0
            pass_modified = 0
            pass_deleted = 0
            pass_skipped = 0
            pass_chunks = 0
            
            # Load files from disk
            disk_paths = set()
            for doc in self.loader.load_documents():
                disk_paths.add(doc.path)
                
                # Check if it has changed
                status = self._has_changed(doc.path, doc.content_hash, db_state)
                
                if status == "unchanged":
                    pass_skipped += 1
                    self.files_skipped += 1
                    continue
                    
                # Needs update
                if status == "modified":
                    await self.store.delete_by_path(doc.path)
                    pass_modified += 1
                    self.files_modified += 1
                else:
                    pass_new += 1
                    
                # Process the file
                chunks = self.chunker.chunk_document(doc)
                if chunks:
                    embeddings = await self.embedder.embed_chunks(chunks)
                    await self.store.upsert_chunks(
                        chunks=chunks, 
                        embeddings=embeddings, 
                        last_modified=doc.last_modified.isoformat() if hasattr(doc.last_modified, 'isoformat') else str(doc.last_modified)
                    )
                    pass_chunks += len(chunks)
                    self.chunks_created += len(chunks)
                    self.files_indexed += 1
                
                # Yield to event loop to keep server responsive
                await asyncio.sleep(0)
                
            # Handle deleted files (in DB but not on disk)
            for path in list(db_state.keys()):
                if path not in disk_paths:
                    await self.store.delete_by_path(path)
                    pass_deleted += 1
                    self.files_deleted += 1
                    
            self.total_files = len(disk_paths)
            self.last_index_time = datetime.now(timezone.utc)
            self.health = "healthy"
            
            logger.info(
                f"Indexed {pass_new + pass_modified + pass_skipped} files ({pass_chunks} chunks). "
                f"{pass_new} new, {pass_modified} modified, {pass_deleted} deleted, {pass_skipped} unchanged."
            )
            
        except Exception as e:
            logger.error(f"Error during indexing: {e}")
            self.health = "error"
        finally:
            self.is_indexing = False
            
    def _group_by_path(self, metadatas: List[Dict[str, Any]]) -> Dict[str, str]:
        """Group existing metadata by source_path to get their hashes.
        
        Returns a dict of path -> content_hash.
        Note: We only need the hash of the first chunk since they all share the document hash.
        """
        paths = {}
        for meta in metadatas:
            path = meta.get("source_path")
            hash_val = meta.get("content_hash")
            if path and hash_val and path not in paths:
                paths[path] = hash_val
        return paths
        
    def _has_changed(self, path: str, current_hash: str, db_state: Dict[str, str]) -> str:
        """Determine if a file has changed compared to DB state."""
        if path not in db_state:
            return "new"
        
        if db_state[path] == current_hash:
            return "unchanged"
            
        return "modified"
