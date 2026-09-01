"""Chat history persistence — SQLite via stdlib sqlite3.

Two tables:
  sessions  — one row per chat session (id, title, created_at, last_active_at)
  messages  — one row per user/assistant exchange (FK → sessions)

Dropped the SQLAlchemy dependency: we only use two simple tables and five
queries. The stdlib sqlite3 module handles it with far less overhead and
zero transitive deps.
"""

import sqlite3
import uuid
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Generator

# Path is under /app/data so the non-root jarvis user can write it
# (the directory is created + chowned in the Dockerfile).
_DB_PATH = "/app/data/history.db"

_CREATE_SESSIONS = """
CREATE TABLE IF NOT EXISTS sessions (
    id           TEXT PRIMARY KEY,
    title        TEXT NOT NULL,
    created_at   TEXT NOT NULL,
    last_active_at TEXT NOT NULL
)
"""

_CREATE_MESSAGES = """
CREATE TABLE IF NOT EXISTS messages (
    id         TEXT PRIMARY KEY,
    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    query      TEXT NOT NULL,
    response   TEXT NOT NULL,
    timestamp  TEXT NOT NULL
)
"""

# Enable WAL mode once at startup for better concurrent read performance.
_PRAGMA_WAL = "PRAGMA journal_mode=WAL"
_PRAGMA_FK  = "PRAGMA foreign_keys=ON"


def _connect() -> sqlite3.Connection:
    Path(_DB_PATH).parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(_DB_PATH, check_same_thread=False)
    conn.row_factory = sqlite3.Row
    conn.execute(_PRAGMA_WAL)
    conn.execute(_PRAGMA_FK)
    return conn


def init_db() -> None:
    """Create tables if they don't exist. Called once on app startup."""
    with _connect() as conn:
        conn.execute(_CREATE_SESSIONS)
        conn.execute(_CREATE_MESSAGES)
        conn.commit()


@contextmanager
def get_db() -> Generator[sqlite3.Connection, None, None]:
    """Yield an open connection; commit on success, rollback on error."""
    conn = _connect()
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


# ---------------------------------------------------------------------------
# Thin data-object wrappers — mimic the old ORM models so callers don't change
# ---------------------------------------------------------------------------

class SessionModel:
    """Plain Python object wrapping a sessions row."""
    __slots__ = ("id", "title", "created_at", "last_active_at")

    def __init__(self, row: sqlite3.Row):
        self.id            = row["id"]
        self.title         = row["title"]
        self.created_at    = _parse_dt(row["created_at"])
        self.last_active_at = _parse_dt(row["last_active_at"])


class MessageModel:
    """Plain Python object wrapping a messages row."""
    __slots__ = ("id", "session_id", "query", "response", "timestamp")

    def __init__(self, row: sqlite3.Row):
        self.id         = row["id"]
        self.session_id = row["session_id"]
        self.query      = row["query"]
        self.response   = row["response"]
        self.timestamp  = _parse_dt(row["timestamp"])


def _parse_dt(value: str) -> datetime:
    """Parse ISO-8601 string from DB into a timezone-aware datetime."""
    try:
        dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except (ValueError, AttributeError):
        return datetime.now(tz=timezone.utc)


def _now_iso() -> str:
    return datetime.now(tz=timezone.utc).isoformat()


# ---------------------------------------------------------------------------
# Query helpers used by the chat router
# ---------------------------------------------------------------------------

def get_all_sessions(conn: sqlite3.Connection) -> list[SessionModel]:
    rows = conn.execute(
        "SELECT * FROM sessions ORDER BY last_active_at DESC"
    ).fetchall()
    return [SessionModel(r) for r in rows]


def get_session(conn: sqlite3.Connection, session_id: str) -> SessionModel | None:
    row = conn.execute(
        "SELECT * FROM sessions WHERE id = ?", (session_id,)
    ).fetchone()
    return SessionModel(row) if row else None


def get_messages(conn: sqlite3.Connection, session_id: str) -> list[MessageModel]:
    rows = conn.execute(
        "SELECT * FROM messages WHERE session_id = ? ORDER BY timestamp ASC",
        (session_id,),
    ).fetchall()
    return [MessageModel(r) for r in rows]


def upsert_session(conn: sqlite3.Connection, session_id: str, title: str) -> None:
    """Create session if not present; update last_active_at if it is."""
    existing = get_session(conn, session_id)
    now = _now_iso()
    if existing is None:
        conn.execute(
            "INSERT INTO sessions (id, title, created_at, last_active_at) VALUES (?,?,?,?)",
            (session_id, title, now, now),
        )
    else:
        conn.execute(
            "UPDATE sessions SET last_active_at = ? WHERE id = ?",
            (now, session_id),
        )


def add_message(
    conn: sqlite3.Connection,
    session_id: str,
    query: str,
    response: str,
    timestamp: str,
) -> None:
    conn.execute(
        "INSERT INTO messages (id, session_id, query, response, timestamp) VALUES (?,?,?,?,?)",
        (str(uuid.uuid4()), session_id, query, response, timestamp),
    )


def delete_session(conn: sqlite3.Connection, session_id: str) -> None:
    # ON DELETE CASCADE handles messages
    conn.execute("DELETE FROM sessions WHERE id = ?", (session_id,))
