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
from app.services.ollama_client import OllamaClient
from app.config import settings
from pathlib import Path

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
                    
                # Generate summary for new/modified files via local LLM
                summary = ""
                try:
                    logger.info(f"Generating summary for {doc.path}...")
                    client = OllamaClient(self.embedder.ollama_url)
                    prompt = f"Write exactly one short sentence summarizing this document. Do not include introductory text, just the summary:\n\n{doc.content[:3000]}"
                    summary_raw, _ = await client.generate(prompt)
                    summary = summary_raw.replace('\n', ' ').strip()
                except Exception as e:
                    logger.warning(f"Failed to generate summary for {doc.path}: {e}")
                    summary = "Summary unavailable."
                    
                # Process the file
                chunks = self.chunker.chunk_document(doc)
                if chunks:
                    embeddings = await self.embedder.embed_chunks(chunks)
                    await self.store.upsert_chunks(
                        chunks=chunks, 
                        embeddings=embeddings, 
                        last_modified=doc.last_modified.isoformat() if hasattr(doc.last_modified, 'isoformat') else str(doc.last_modified),
                        summary=summary
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
                    
            # Generate Global Memory Index if things changed
            if pass_new > 0 or pass_modified > 0 or pass_deleted > 0:
                await self._update_global_memory()
                    
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

    async def _update_global_memory(self) -> None:
        """Fetch all path summaries from Chroma and compile a global index."""
        try:
            logger.info("Updating global memory index...")
            metadatas = await self.store.get_all_metadata()
            path_summaries = {}
            for meta in metadatas:
                path = meta.get("source_path")
                summary = meta.get("summary", "Summary unavailable.")
                if path and path not in path_summaries:
                    path_summaries[path] = summary
                    
            if not path_summaries:
                return
                
            memory_dir = Path(settings.vault_path) / "Memory"
            memory_dir.mkdir(parents=True, exist_ok=True)
            index_path = memory_dir / "global_index.md"
            
            content = ["# AI Global Memory Index\n", "This file contains a persistent map of all documents inside the vault.\n\n"]
            for path in sorted(path_summaries.keys()):
                content.append(f"- **{path}**: {path_summaries[path]}")
                
            index_path.write_text("\n".join(content), encoding="utf-8")
            logger.info("Successfully updated Memory/global_index.md")
        except Exception as e:
            logger.error(f"Failed to update global memory: {e}")
