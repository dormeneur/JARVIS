"""Chat history API — sync, list sessions, get messages, delete.

Imports only the chat router (not the full app) so tests run without
tiktoken/PyMuPDF/chromadb-client installed in the local dev venv.
"""
import pytest
from unittest.mock import patch
from fastapi import FastAPI
from fastapi.testclient import TestClient

import app.services.history_db as history_db_module
from app.services.history_db import init_db
from app.routers.chat import router as chat_router


def _make_client(db_path: str) -> TestClient:
    with patch.object(history_db_module, "_DB_PATH", db_path):
        init_db()
        mini_app = FastAPI()
        mini_app.include_router(chat_router)
        return TestClient(mini_app)


@pytest.fixture
def client(tmp_path):
    db_path = str(tmp_path / "test_history.db")
    with patch.object(history_db_module, "_DB_PATH", db_path):
        init_db()
        mini_app = FastAPI()
        mini_app.include_router(chat_router)
        yield TestClient(mini_app)


def test_chat_sessions_full_flow(client):
    """Sync → list → get history → delete → verify gone."""
    session_id = "test-session-123"
    payload = {
        "session_id": session_id,
        "query": "Hello Jarvis, how are you?",
        "response": "I am doing well, thank you!",
        "timestamp": "2026-04-14T12:00:00Z",
    }

    resp = client.post("/brain/chat/sync", json=payload)
    assert resp.status_code == 200
    assert resp.json() == {"status": "ok"}

    resp = client.get("/brain/chat/sessions")
    assert resp.status_code == 200
    sessions = resp.json()
    assert len(sessions) == 1
    assert sessions[0]["id"] == session_id
    assert sessions[0]["title"] == "Hello Jarvis, how are you?"

    resp = client.get(f"/brain/chat/sessions/{session_id}")
    assert resp.status_code == 200
    history = resp.json()
    assert len(history) == 1
    assert history[0]["query"] == payload["query"]
    assert history[0]["response"] == payload["response"]

    resp = client.delete(f"/brain/chat/sessions/{session_id}")
    assert resp.status_code == 200

    assert client.get("/brain/chat/sessions").json() == []
    assert client.get(f"/brain/chat/sessions/{session_id}").status_code == 404


def test_delete_nonexistent_returns_404(client):
    assert client.delete("/brain/chat/sessions/does-not-exist").status_code == 404


def test_multiple_messages_per_session(client):
    session_id = "multi"
    for i in range(3):
        client.post("/brain/chat/sync", json={
            "session_id": session_id,
            "query": f"Question {i}",
            "response": f"Answer {i}",
            "timestamp": f"2026-04-14T12:0{i}:00Z",
        })

    msgs = client.get(f"/brain/chat/sessions/{session_id}").json()
    assert len(msgs) == 3
    for i, m in enumerate(msgs):
        assert m["query"] == f"Question {i}"


def test_multiple_sessions_ordered_by_last_active(client):
    for sid, ts in [("old", "2026-01-01T00:00:00Z"), ("new", "2026-06-01T00:00:00Z")]:
        client.post("/brain/chat/sync", json={
            "session_id": sid, "query": "Q", "response": "A", "timestamp": ts,
        })

    sessions = client.get("/brain/chat/sessions").json()
    ids = [s["id"] for s in sessions]
    assert ids.index("new") < ids.index("old")
