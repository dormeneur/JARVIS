"""Vector store in SQLite, brute-force cosine: the same interface as VectorStore (ChromaDB).

At personal scale (thousands of chunks x 768 dims is a few MB and one matrix product, well under
50 ms) an approximate index buys nothing, and this removes a container, an old pinned server and
the one piece of state that lived outside the vault: the whole index sits in
<vault>/.system/brain/vectors.db, hidden from sync and indexing and included in backups.

query() returns ChromaDB's response shape, so Retriever and the rest need no changes.
journal_mode=DELETE, not WAL (see keyword_index.py).
"""
from __future__ import annotations

import asyncio
import json
import sqlite3
from pathlib import Path
from typing import Any, AsyncIterator, Dict, List, Optional, Sequence, Tuple

import numpy as np

from app.services.text_chunker import Chunk


class SqliteVectorStore:
    def __init__(self, db_path: Path):
        self.db_path = Path(db_path)
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as conn:
            conn.execute(
                "CREATE TABLE IF NOT EXISTS vectors ("
                "chunk_id TEXT PRIMARY KEY, source_path TEXT NOT NULL, document TEXT NOT NULL, "
                "meta TEXT NOT NULL, embedding BLOB NOT NULL)"
            )
            conn.execute("CREATE INDEX IF NOT EXISTS vectors_path ON vectors(source_path)")
        # (ids, paths, matrix of unit vectors); rebuilt lazily after any write.
        self._cache: Optional[Tuple[List[str], List[str], np.ndarray]] = None

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.db_path, timeout=30)
        conn.execute("PRAGMA journal_mode=DELETE")
        return conn

    @staticmethod
    def _unit(vector: Sequence[float]) -> np.ndarray:
        v = np.asarray(vector, dtype=np.float32)
        norm = float(np.linalg.norm(v))
        return v / norm if norm else v

    # -- sync bodies (run in a worker thread) -------------------------------------------------

    def _upsert(self, rows: List[tuple]) -> None:
        with self._connect() as conn:
            conn.executemany(
                "INSERT INTO vectors(chunk_id, source_path, document, meta, embedding) VALUES (?,?,?,?,?) "
                "ON CONFLICT(chunk_id) DO UPDATE SET source_path=excluded.source_path, "
                "document=excluded.document, meta=excluded.meta, embedding=excluded.embedding",
                rows,
            )
        self._cache = None

    def _delete(self, path: str) -> None:
        with self._connect() as conn:
            conn.execute("DELETE FROM vectors WHERE source_path = ?", (path,))
        self._cache = None

    def _matrix(self) -> Tuple[List[str], List[str], np.ndarray]:
        if self._cache is None:
            with self._connect() as conn:
                rows = conn.execute("SELECT chunk_id, source_path, embedding FROM vectors").fetchall()
            ids = [r[0] for r in rows]
            paths = [r[1] for r in rows]
            matrix = (
                np.vstack([np.frombuffer(r[2], dtype=np.float32) for r in rows])
                if rows else np.zeros((0, 1), dtype=np.float32)
            )
            self._cache = (ids, paths, matrix)
        return self._cache

    def _query(self, embedding: List[float], top_k: int, filter_paths: Optional[List[str]]) -> Dict[str, Any]:
        ids, paths, matrix = self._matrix()
        empty = {"ids": [[]], "distances": [[]], "documents": [[]], "metadatas": [[]]}
        if not ids:
            return empty
        sims = matrix @ self._unit(embedding)
        if filter_paths:
            allowed = set(filter_paths)
            mask = np.fromiter((p in allowed for p in paths), dtype=bool, count=len(paths))
            sims = np.where(mask, sims, -np.inf)
        order = [int(i) for i in np.argsort(-sims)[:top_k] if np.isfinite(sims[i])]
        if not order:
            return empty
        chosen = [ids[i] for i in order]
        with self._connect() as conn:
            found = {
                r[0]: (r[1], json.loads(r[2]))
                for r in conn.execute(
                    f"SELECT chunk_id, document, meta FROM vectors WHERE chunk_id IN ({','.join('?' * len(chosen))})",
                    chosen,
                )
            }
        return {
            "ids": [chosen],
            "distances": [[float(1.0 - sims[i]) for i in order]],  # cosine distance, like Chroma
            "documents": [[found[c][0] for c in chosen]],
            "metadatas": [[found[c][1] for c in chosen]],
        }

    def _all(self, with_embeddings: bool) -> List[tuple]:
        with self._connect() as conn:
            rows = conn.execute("SELECT chunk_id, document, meta, embedding FROM vectors").fetchall()
        out = []
        for cid, doc, meta, emb in rows:
            item = (cid, doc, json.loads(meta))
            out.append(item + (np.frombuffer(emb, dtype=np.float32).tolist(),) if with_embeddings else item)
        return out

    def _count(self) -> int:
        with self._connect() as conn:
            return conn.execute("SELECT count(*) FROM vectors").fetchone()[0]

    # -- the VectorStore interface ---------------------------------------------------------------

    async def upsert_chunks(
        self, chunks: List[Chunk], embeddings: List[List[float]], last_modified: str,
        summary: str = "", file_hash: str = "", index_version: int = 0,
    ) -> None:
        if not chunks or not embeddings:
            return
        if len(chunks) != len(embeddings):
            raise ValueError(f"Mismatched chunks ({len(chunks)}) and embeddings ({len(embeddings)})")
        rows = []
        for c, emb in zip(chunks, embeddings):
            meta = {
                "source_path": c.source_path, "chunk_index": c.chunk_index, "content_hash": c.content_hash,
                "file_hash": file_hash, "index_version": index_version,
                "last_modified": last_modified, "summary": summary,
            }
            rows.append((c.chunk_id, c.source_path, c.content, json.dumps(meta), self._unit(emb).tobytes()))
        await asyncio.to_thread(self._upsert, rows)

    async def import_rows(self, rows: List[Tuple[str, str, Dict[str, Any], List[float]]]) -> None:
        """(chunk_id, document, metadata, embedding) tuples, e.g. when migrating from ChromaDB."""
        await asyncio.to_thread(
            self._upsert,
            [(cid, meta.get("source_path", ""), doc or "", json.dumps(meta), self._unit(emb).tobytes())
             for cid, doc, meta, emb in rows],
        )

    async def delete_by_path(self, source_path: str) -> None:
        await asyncio.to_thread(self._delete, source_path)

    async def query(
        self, query_embedding: List[float], top_k: int = 5, filter_paths: Optional[List[str]] = None
    ) -> Dict[str, Any]:
        return await asyncio.to_thread(self._query, query_embedding, top_k, filter_paths)

    async def get_all_metadata(self) -> List[Dict[str, Any]]:
        return [meta for _, _, meta in await asyncio.to_thread(self._all, False)]

    async def iter_all(self, batch: int = 500) -> AsyncIterator[Tuple[str, str, Dict[str, Any]]]:
        for item in await asyncio.to_thread(self._all, False):
            yield item

    async def count(self) -> int:
        return await asyncio.to_thread(self._count)


async def migrate_from_chroma(chroma: Any, target: SqliteVectorStore, batch: int = 200) -> int:
    """Copy every chunk (with its stored embedding) from ChromaDB. No re-embedding, so the model is
    not touched. Returns how many chunks were copied."""
    copied = 0
    rows: List[tuple] = []
    async for cid, doc, meta, emb in chroma.iter_all_with_embeddings():
        rows.append((cid, doc, meta, emb))
        if len(rows) >= batch:
            await target.import_rows(rows)
            copied += len(rows)
            rows = []
    if rows:
        await target.import_rows(rows)
        copied += len(rows)
    return copied
