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
                    last_hash TEXT NOT NULL,
                    prev_hash TEXT
                )
            """)
            # Migrate: add prev_hash column if it doesn't exist (existing DBs)
            cursor = conn.execute("PRAGMA table_info(file_versions)")
            columns = [row[1] for row in cursor.fetchall()]
            if "prev_hash" not in columns:
                conn.execute("ALTER TABLE file_versions ADD COLUMN prev_hash TEXT")
                # Safe default: set prev_hash = last_hash for existing rows
                conn.execute("UPDATE file_versions SET prev_hash = last_hash")
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

    def get_prev_hash(self, path: str) -> Optional[str]:
        """Get the previous content hash for a file (before the latest edit)."""
        with sqlite3.connect(self.db_path) as conn:
            conn.row_factory = None
            cursor = conn.execute(
                "SELECT prev_hash FROM file_versions WHERE path = ?",
                (path,)
            )
            row = cursor.fetchone()
            cursor.close()
            return row[0] if row else None

    def create_version(self, path: str, content_hash: str) -> int:
        """Create initial version entry for a new file. Returns version 1."""
        with sqlite3.connect(self.db_path) as conn:
            conn.execute(
                "INSERT INTO file_versions (path, version, last_hash, prev_hash) VALUES (?, 1, ?, NULL)",
                (path, content_hash)
            )
            conn.commit()
        return 1

    def increment_version(self, path: str, new_hash: str) -> int:
        """Increment version for an existing file. Returns new version number."""
        with sqlite3.connect(self.db_path) as conn:
            conn.row_factory = None
            cursor = conn.execute(
                "SELECT version, last_hash FROM file_versions WHERE path = ?",
                (path,)
            )
            row = cursor.fetchone()
            cursor.close()
            
            if row is None:
                # Previously untracked file being modified for the first time.
                # Use version 2 so it differs from any client that knows version 1.
                new_version = 2
                conn.execute(
                    "INSERT INTO file_versions (path, version, last_hash, prev_hash) VALUES (?, ?, ?, NULL)",
                    (path, new_version, new_hash)
                )
                conn.commit()
                return new_version
            
            old_hash = row[1]
            new_version = row[0] + 1
            conn.execute(
                "UPDATE file_versions SET version = ?, last_hash = ?, prev_hash = ? WHERE path = ?",
                (new_version, new_hash, old_hash, path)
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
            # Read old hash before upserting so prev_hash is preserved
            cursor = conn.execute(
                "SELECT last_hash FROM file_versions WHERE path = ?",
                (path,)
            )
            row = cursor.fetchone()
            cursor.close()
            old_hash = row[0] if row else None

            conn.execute(
                """
                INSERT INTO file_versions (path, version, last_hash, prev_hash)
                VALUES (?, ?, ?, ?)
                ON CONFLICT(path) DO UPDATE SET
                    version = excluded.version,
                    last_hash = excluded.last_hash,
                    prev_hash = ?
                """,
                (path, version, content_hash, old_hash, old_hash)
            )
            conn.commit()
