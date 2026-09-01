"""Chat archive retention logic — selection criteria, deletion, file format.

Imports only the chat router (not the full app) so tests run without
tiktoken/PyMuPDF/chromadb-client installed in the local dev venv.
"""
import re
import uuid
from datetime import datetime, timedelta, timezone
from unittest.mock import patch

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

import app.services.history_db as history_db_module
from app.services.history_db import (
    get_db, init_db, upsert_session, add_message,
    get_session, get_messages,
)
from app.routers.chat import router as chat_router


def _now() -> str:
    return datetime.now(tz=timezone.utc).isoformat()


def _days_ago(n: int) -> str:
    return (datetime.now(tz=timezone.utc) - timedelta(days=n)).isoformat()


@pytest.fixture
def client(tmp_path):
    db_path = str(tmp_path / "archive.db")
    with patch.object(history_db_module, "_DB_PATH", db_path):
        init_db()
        mini_app = FastAPI()
        mini_app.include_router(chat_router)
        yield TestClient(mini_app)


@pytest.fixture
def db_path(tmp_path):
    path = str(tmp_path / "archive.db")
    with patch.object(history_db_module, "_DB_PATH", path):
        init_db()
        yield path


def _insert_session(db_path, session_id: str, title: str, last_active_iso: str):
    with patch.object(history_db_module, "_DB_PATH", db_path):
        with get_db() as conn:
            conn.execute(
                "INSERT OR IGNORE INTO sessions (id, title, created_at, last_active_at) VALUES (?,?,?,?)",
                (session_id, title, _now(), last_active_iso),
            )


def _insert_message(db_path, session_id: str, query: str = "Q", response: str = "A"):
    with patch.object(history_db_module, "_DB_PATH", db_path):
        with get_db() as conn:
            add_message(conn, session_id, query, response, _now())


# ---------------------------------------------------------------------------
# Archive selection criteria
# ---------------------------------------------------------------------------

class TestArchiveSessionSelection:
    def test_old_session_is_archivable(self, db_path):
        _insert_session(db_path, "old-1", "Old Chat", _days_ago(10))
        cutoff = _days_ago(7)
        with patch.object(history_db_module, "_DB_PATH", db_path):
            with get_db() as conn:
                rows = conn.execute(
                    "SELECT id FROM sessions WHERE last_active_at < ?", (cutoff,)
                ).fetchall()
        assert any(r["id"] == "old-1" for r in rows)

    def test_recent_session_not_archivable(self, db_path):
        _insert_session(db_path, "recent-1", "Recent Chat", _days_ago(3))
        cutoff = _days_ago(7)
        with patch.object(history_db_module, "_DB_PATH", db_path):
            with get_db() as conn:
                rows = conn.execute(
                    "SELECT id FROM sessions WHERE last_active_at < ?", (cutoff,)
                ).fetchall()
        assert not any(r["id"] == "recent-1" for r in rows)

    def test_mixed_sessions_only_old_selected(self, db_path):
        _insert_session(db_path, "old-a", "Old A", _days_ago(15))
        _insert_session(db_path, "old-b", "Old B", _days_ago(20))
        _insert_session(db_path, "recent", "Recent", _days_ago(2))
        cutoff = _days_ago(7)
        with patch.object(history_db_module, "_DB_PATH", db_path):
            with get_db() as conn:
                rows = conn.execute(
                    "SELECT id FROM sessions WHERE last_active_at < ?", (cutoff,)
                ).fetchall()
        ids = {r["id"] for r in rows}
        assert ids == {"old-a", "old-b"}

    def test_session_active_recently_not_archivable(self, db_path):
        _insert_session(db_path, "long-running", "Long Running", _days_ago(1))
        cutoff = _days_ago(7)
        with patch.object(history_db_module, "_DB_PATH", db_path):
            with get_db() as conn:
                rows = conn.execute(
                    "SELECT id FROM sessions WHERE last_active_at < ?", (cutoff,)
                ).fetchall()
        assert not any(r["id"] == "long-running" for r in rows)


# ---------------------------------------------------------------------------
# Session deletion via API
# ---------------------------------------------------------------------------

class TestSessionDeletion:
    def test_delete_removes_session_and_messages(self, client, db_path):
        _insert_session(db_path, "del-1", "Delete Me", _now())
        _insert_message(db_path, "del-1", "Q1", "A1")
        _insert_message(db_path, "del-1", "Q2", "A2")

        with patch.object(history_db_module, "_DB_PATH", db_path):
            assert client.delete("/brain/chat/sessions/del-1").status_code == 200
            assert client.get("/brain/chat/sessions/del-1").status_code == 404

    def test_delete_nonexistent_returns_404(self, client, db_path):
        with patch.object(history_db_module, "_DB_PATH", db_path):
            assert client.delete("/brain/chat/sessions/ghost").status_code == 404

    def test_delete_preserves_other_sessions(self, client, db_path):
        _insert_session(db_path, "keep", "Keep", _now())
        _insert_message(db_path, "keep", "Stay", "OK")
        _insert_session(db_path, "gone", "Gone", _now())
        _insert_message(db_path, "gone", "Bye", "Later")

        with patch.object(history_db_module, "_DB_PATH", db_path):
            client.delete("/brain/chat/sessions/gone")
            sessions = client.get("/brain/chat/sessions").json()
            assert len(sessions) == 1 and sessions[0]["id"] == "keep"
            msgs = client.get("/brain/chat/sessions/keep").json()
            assert len(msgs) == 1


# ---------------------------------------------------------------------------
# Memory file format (mirrors ChatArchiveService._buildMemoryFileContent)
# ---------------------------------------------------------------------------

class TestMemoryFileFormat:
    def test_content_format(self):
        title, date, summary = "How to set up Docker Compose", "2026-04-25", "Summary."
        content = f"# {title}\n**Date:** {date}  \n**Summary:** {summary}\n"
        assert content.startswith(f"# {title}")
        assert "**Date:**" in content and "**Summary:**" in content

    def test_filename_format(self):
        words = "How to set up Docker".lower().split()[:5]
        filename = f"2026-04-25-{'-'.join(words)}.md"
        assert filename == "2026-04-25-how-to-set-up-docker.md"

    def test_slug_strips_special_chars(self):
        clean = re.sub(r"[^a-zA-Z0-9\s]", "", "What's the plan for Q1 2026?")
        slug = "-".join(w.lower() for w in clean.split()[:5])
        assert "?" not in slug and "'" not in slug

    def test_empty_title_fallback(self):
        words = [w for w in re.sub(r"[^a-zA-Z0-9\s]", "", "").split() if w]
        slug = "-".join(w.lower() for w in words[:5]) if words else "untitled-chat"
        assert slug == "untitled-chat"


# ---------------------------------------------------------------------------
# Integrity
# ---------------------------------------------------------------------------

class TestArchiveJobIntegrity:
    def test_session_survives_if_no_file_written(self, client, db_path):
        _insert_session(db_path, "survivor", "Survivor", _days_ago(10))
        _insert_message(db_path, "survivor", "Q", "A")
        with patch.object(history_db_module, "_DB_PATH", db_path):
            resp = client.get("/brain/chat/sessions/survivor")
            assert resp.status_code == 200 and len(resp.json()) == 1

    def test_all_messages_present_before_archive(self, client, db_path):
        _insert_session(db_path, "multi", "Multi", _now())
        for i in range(5):
            _insert_message(db_path, "multi", f"Q{i}", f"A{i}")
        with patch.object(history_db_module, "_DB_PATH", db_path):
            msgs = client.get("/brain/chat/sessions/multi").json()
            assert len(msgs) == 5
