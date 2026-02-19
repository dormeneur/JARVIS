"""
Version tracking for optimistic concurrency control.

Each file in the vault has a version number that increments on every write.
This enables detection of concurrent edits across devices without relying on timestamps.
"""

from __future__ import annotations

import sqlite3
from pathlib import Path
from typing import Optional

from app.config import settings


class VersionTracker:
    """Manages file version numbers for conflict detection."""

    def __init__(self, db_path: Optional[Path] = None):
        if db_path is None:
            db_path = settings.vault_path / "system" / "file_versions.db"
        self.db_path = db_path
        self._ensure_db()

    def _ensure_db(self) -> None:
        """Create database and table if they don't exist."""
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        
        with sqlite3.connect(self.db_path) as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS file_versions (
                    path TEXT PRIMARY KEY,
                    version INTEGER NOT NULL,
                    last_hash TEXT NOT NULL
                )
            """)
            conn.commit()

    def get_version(self, path: str) -> Optional[int]:
        """Get the current version for a file path."""
        with sqlite3.connect(self.db_path) as conn:
            conn.row_factory = None
            cursor = conn.execute(
                "SELECT version FROM file_versions WHERE path = ?",
                (path,)
            )
            row = cursor.fetchone()
            cursor.close()
            return row[0] if row else None

    def get_version_and_hash(self, path: str) -> Optional[tuple[int, str]]:
        """Get the current version and hash for a file path."""
        with sqlite3.connect(self.db_path) as conn:
            conn.row_factory = None
            cursor = conn.execute(
                "SELECT version, last_hash FROM file_versions WHERE path = ?",
                (path,)
            )
            row = cursor.fetchone()
            cursor.close()
            return (row[0], row[1]) if row else None

    def create_version(self, path: str, content_hash: str) -> int:
        """Create initial version entry for a new file. Returns version 1."""
        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                "INSERT INTO file_versions (path, version, last_hash) VALUES (?, 1, ?)",
                (path, content_hash)
            )
            conn.commit()
        return 1

    def increment_version(self, path: str, new_hash: str) -> int:
        """Increment version for an existing file. Returns new version number."""
        with sqlite3.connect(self.db_path) as conn:
            conn.row_factory = None
            cursor = conn.execute(
                "SELECT version FROM file_versions WHERE path = ?",
                (path,)
            )
            row = cursor.fetchone()
            cursor.close()
            
            if row is None:
                # File doesn't exist in version tracking, create it
                return self.create_version(path, new_hash)
            
            new_version = row[0] + 1
            conn.execute(
                "UPDATE file_versions SET version = ?, last_hash = ? WHERE path = ?",
                (new_version, new_hash, path)
            )
            conn.commit()
            return new_version

    def delete_version(self, path: str) -> None:
        """Remove version tracking for a deleted file."""
        with sqlite3.connect(self.db_path) as conn:
            conn.execute("DELETE FROM file_versions WHERE path = ?", (path,))
            conn.commit()

    def upsert_version(self, path: str, version: int, content_hash: str) -> None:
        """Insert or update version entry (used for initialization/migration)."""
        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                """
                INSERT INTO file_versions (path, version, last_hash)
                VALUES (?, ?, ?)
                ON CONFLICT(path) DO UPDATE SET
                    version = excluded.version,
                    last_hash = excluded.last_hash
                """,
                (path, version, content_hash)
            )
            conn.commit()
