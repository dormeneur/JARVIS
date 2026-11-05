"""Keyword (BM25) index over the same chunks as the vector store, using SQLite FTS5.

Vector search alone misses names, IDs and exact phrases; BM25 finds them. FTS5 ships with Python's
sqlite3, so there is no new dependency. The index is derived data: if it ever disagrees with the
vector store, the indexer rebuilds it from there.

Lives in <vault>/.system/brain/fts.db (hidden from sync and indexing, included in backups).
journal_mode=DELETE, not WAL: WAL's shared-memory file is unreliable on Docker Desktop bind mounts.
"""
from __future__ import annotations

import asyncio
import re
import sqlite3
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Sequence

MAX_QUERY_TOKENS = 12

# Words that match everything and rank nothing.
_STOPWORDS = frozenset(
    "a about all am an and are as at be by did do does for from has have how i in is it list me my "
    "of on or s show tell that the there this to was were what whats when where which who with".split()
)


def query_tokens(query: str) -> List[str]:
    tokens = [t for t in re.findall(r"\w+", query.casefold()) if len(t) >= 2 and t not in _STOPWORDS]
    seen: set = set()
    unique = [t for t in tokens if not (t in seen or seen.add(t))]
    return unique[:MAX_QUERY_TOKENS]


def fts_query(query: str) -> str:
    """Quote every token so FTS5 syntax characters in a question can never raise an error."""
    return " OR ".join(f'"{t}"' for t in query_tokens(query))


class KeywordIndex:
    def __init__(self, db_path: Path):
        self.db_path = Path(db_path)
        self._ensure()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.db_path, timeout=30)
        conn.execute("PRAGMA journal_mode=DELETE")
        return conn

    def _ensure(self) -> None:
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        with self._connect() as conn:
            conn.execute(
                "CREATE VIRTUAL TABLE IF NOT EXISTS chunks USING fts5("
                "chunk_id UNINDEXED, source_path UNINDEXED, chunk_index UNINDEXED, content, "
                "tokenize='porter unicode61 remove_diacritics 2')"
            )

    # -- sync bodies, run in a worker thread ------------------------------------------------

    def _upsert(self, rows: List[tuple]) -> None:
        with self._connect() as conn:
            conn.executemany("DELETE FROM chunks WHERE chunk_id = ?", [(r[0],) for r in rows])
            conn.executemany("INSERT INTO chunks(chunk_id, source_path, chunk_index, content) VALUES (?,?,?,?)", rows)

    def _delete_by_path(self, path: str) -> None:
        with self._connect() as conn:
            conn.execute("DELETE FROM chunks WHERE source_path = ?", (path,))

    def _count(self) -> int:
        with self._connect() as conn:
            return conn.execute("SELECT count(*) FROM chunks").fetchone()[0]

    def _clear(self) -> None:
        with self._connect() as conn:
            conn.execute("DELETE FROM chunks")

    def _search(self, query: str, limit: int, filter_paths: Optional[Sequence[str]]) -> List[Dict[str, Any]]:
        match = fts_query(query)
        if not match:
            return []
        sql = "SELECT chunk_id, source_path, chunk_index, content FROM chunks WHERE chunks MATCH ?"
        params: List[Any] = [match]
        if filter_paths:
            sql += f" AND source_path IN ({','.join('?' * len(filter_paths))})"
            params += list(filter_paths)
        sql += " ORDER BY bm25(chunks) LIMIT ?"
        params.append(limit)
        with self._connect() as conn:
            rows = conn.execute(sql, params).fetchall()
        return [{"chunk_id": r[0], "path": r[1], "chunk_index": r[2], "content": r[3]} for r in rows]

    # -- public async API ---------------------------------------------------------------------

    async def upsert(self, chunks: Iterable[Any]) -> None:
        rows = [(c.chunk_id, c.source_path, c.chunk_index, c.content) for c in chunks]
        if rows:
            await asyncio.to_thread(self._upsert, rows)

    async def upsert_rows(self, rows: List[tuple]) -> None:
        """(chunk_id, source_path, chunk_index, content) tuples, for rebuilding from the vector store."""
        if rows:
            await asyncio.to_thread(self._upsert, rows)

    async def delete_by_path(self, path: str) -> None:
        await asyncio.to_thread(self._delete_by_path, path)

    async def count(self) -> int:
        return await asyncio.to_thread(self._count)

    async def clear(self) -> None:
        await asyncio.to_thread(self._clear)

    async def search(self, query: str, limit: int = 20, filter_paths: Optional[Sequence[str]] = None) -> List[Dict[str, Any]]:
        return await asyncio.to_thread(self._search, query, limit, filter_paths)
