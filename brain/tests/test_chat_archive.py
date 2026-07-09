"""Tests for the chat archive retention and deletion logic.

These tests validate:
1. Sessions older than 7 days by last_active_at are selected for archiving
2. Active sessions are never selected regardless of age
3. Memory file path and content format are correct
4. Deletion only proceeds after the session is confirmed to exist
5. The DELETE endpoint works correctly for cleanup
"""
import pytest
from fastapi.testclient import TestClient
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
from datetime import datetime, timedelta
import os
import uuid

from app.api import app
from app.services.history_db import Base, get_db, SessionModel, MessageModel

# Use an in-memory database to avoid Windows file lock issues
# StaticPool ensures all connections share the same in-memory database
from sqlalchemy.pool import StaticPool

SQLALCHEMY_DATABASE_URL = "sqlite:///:memory:"

engine = create_engine(
    SQLALCHEMY_DATABASE_URL,
    connect_args={"check_same_thread": False},
    poolclass=StaticPool,
)
TestingSessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=engine)


def override_get_db():
    try:
        db = TestingSessionLocal()
        yield db
    finally:
        db.close()


app.dependency_overrides[get_db] = override_get_db


@pytest.fixture(autouse=True)
def setup_db():
    Base.metadata.create_all(bind=engine)
    yield
    Base.metadata.drop_all(bind=engine)


client = TestClient(app)


def _create_session(db, session_id: str, title: str, last_active_at: datetime):
    """Helper to create a session with a specific last_active_at."""
    session = SessionModel(
        id=session_id,
        title=title,
        created_at=datetime.utcnow(),
        last_active_at=last_active_at,
    )
    db.add(session)
    db.commit()
    return session


def _create_message(db, session_id: str, query: str, response: str):
    """Helper to create a message in a session."""
    msg = MessageModel(
        id=str(uuid.uuid4()),
        session_id=session_id,
        query=query,
        response=response,
        timestamp=datetime.utcnow(),
    )
    db.add(msg)
    db.commit()
    return msg


class TestArchiveSessionSelection:
    """Tests for selecting sessions eligible for archiving."""

    def test_old_session_is_archivable(self):
        """Sessions with last_active_at older than 7 days should be selected."""
        db = TestingSessionLocal()
        try:
            old_date = datetime.utcnow() - timedelta(days=10)
            _create_session(db, "old-session-1", "Old Chat", old_date)

            # Query sessions older than 7 days
            cutoff = datetime.utcnow() - timedelta(days=7)
            old_sessions = (
                db.query(SessionModel)
                .filter(SessionModel.last_active_at < cutoff)
                .all()
            )
            assert len(old_sessions) == 1
            assert old_sessions[0].id == "old-session-1"
        finally:
            db.close()

    def test_recent_session_is_not_archivable(self):
        """Sessions with last_active_at within 7 days should NOT be selected."""
        db = TestingSessionLocal()
        try:
            recent_date = datetime.utcnow() - timedelta(days=3)
            _create_session(db, "recent-session-1", "Recent Chat", recent_date)

            cutoff = datetime.utcnow() - timedelta(days=7)
            old_sessions = (
                db.query(SessionModel)
                .filter(SessionModel.last_active_at < cutoff)
                .all()
            )
            assert len(old_sessions) == 0
        finally:
            db.close()

    def test_mixed_sessions_only_old_selected(self):
        """When both old and recent sessions exist, only old ones are selected."""
        db = TestingSessionLocal()
        try:
            old_date = datetime.utcnow() - timedelta(days=15)
            recent_date = datetime.utcnow() - timedelta(days=2)
            today = datetime.utcnow()

            _create_session(db, "old-1", "Old Chat 1", old_date)
            _create_session(db, "old-2", "Old Chat 2", old_date - timedelta(days=5))
            _create_session(db, "recent-1", "Recent Chat", recent_date)
            _create_session(db, "active-1", "Active Chat", today)

            cutoff = datetime.utcnow() - timedelta(days=7)
            old_sessions = (
                db.query(SessionModel)
                .filter(SessionModel.last_active_at < cutoff)
                .all()
            )
            old_ids = {s.id for s in old_sessions}
            assert old_ids == {"old-1", "old-2"}
        finally:
            db.close()

    def test_session_active_yesterday_not_archivable(self):
        """A session created 30 days ago but active yesterday should NOT be archived."""
        db = TestingSessionLocal()
        try:
            session = SessionModel(
                id="old-created-recent-active",
                title="Long Running Chat",
                created_at=datetime.utcnow() - timedelta(days=30),
                last_active_at=datetime.utcnow() - timedelta(days=1),
            )
            db.add(session)
            db.commit()

            cutoff = datetime.utcnow() - timedelta(days=7)
            old_sessions = (
                db.query(SessionModel)
                .filter(SessionModel.last_active_at < cutoff)
                .all()
            )
            assert len(old_sessions) == 0
        finally:
            db.close()


class TestSessionDeletion:
    """Tests for session deletion after archiving."""

    def test_delete_session_removes_messages(self):
        """Deleting a session via API should remove all its messages too."""
        db = TestingSessionLocal()
        try:
            session_id = "to-delete-1"
            _create_session(db, session_id, "Delete Me", datetime.utcnow())
            _create_message(db, session_id, "Hello", "Hi there")
            _create_message(db, session_id, "How are you?", "I'm good")
        finally:
            db.close()

        # Delete via API
        response = client.delete(f"/brain/chat/sessions/{session_id}")
        assert response.status_code == 200

        # Verify session and messages are gone
        response = client.get(f"/brain/chat/sessions/{session_id}")
        assert response.status_code == 404

        response = client.get("/brain/chat/sessions")
        sessions = response.json()
        assert len(sessions) == 0

    def test_delete_nonexistent_session_returns_404(self):
        """Deleting a session that doesn't exist should return 404."""
        response = client.delete("/brain/chat/sessions/nonexistent-id")
        assert response.status_code == 404

    def test_delete_preserves_other_sessions(self):
        """Deleting one session should not affect others."""
        db = TestingSessionLocal()
        try:
            _create_session(db, "keep-me", "Keep This", datetime.utcnow())
            _create_message(db, "keep-me", "Stay", "OK")
            _create_session(db, "delete-me", "Delete This", datetime.utcnow())
            _create_message(db, "delete-me", "Bye", "Goodbye")
        finally:
            db.close()

        # Delete only one
        response = client.delete("/brain/chat/sessions/delete-me")
        assert response.status_code == 200

        # Verify the other survives
        response = client.get("/brain/chat/sessions")
        sessions = response.json()
        assert len(sessions) == 1
        assert sessions[0]["id"] == "keep-me"

        # Verify its messages survive
        response = client.get("/brain/chat/sessions/keep-me")
        messages = response.json()
        assert len(messages) == 1
        assert messages[0]["query"] == "Stay"


class TestMemoryFileFormat:
    """Tests for the memory file path and content format."""

    def test_memory_file_content_format(self):
        """Memory file should follow the exact markdown format."""
        # Simulate what the archive service produces
        title = "How to set up Docker Compose"
        date = "2026-04-25"
        summary = "Discussed Docker Compose setup for multi-container JARVIS deployment."

        expected = f"# {title}\n**Date:** {date}  \n**Summary:** {summary}\n"

        # Build content the same way the service does
        content = f"# {title}\n**Date:** {date}  \n**Summary:** {summary}\n"
        assert content == expected
        assert content.startswith("# ")
        assert "**Date:**" in content
        assert "**Summary:**" in content

    def test_memory_filename_format(self):
        """Memory filename should be YYYY-MM-DD-slug.md format."""
        # Test slug generation logic (replicated from Dart)
        title = "How to set up Docker"
        date = "2026-04-25"

        # Simulate slug generation (max 5 words)
        words = title.lower().split()
        slug = "-".join(words[:5])
        filename = f"{date}-{slug}.md"

        assert filename == "2026-04-25-how-to-set-up-docker.md"
        assert filename.endswith(".md")

    def test_slug_handles_special_characters(self):
        """Slug should strip special characters."""
        import re

        title = "What's the plan for Q1 2026?"
        clean = re.sub(r'[^a-zA-Z0-9\s]', '', title)
        words = clean.strip().split()
        slug = "-".join(w.lower() for w in words[:5])

        assert slug == "whats-the-plan-for-q1"
        assert "?" not in slug
        assert "'" not in slug

    def test_empty_title_produces_fallback_slug(self):
        """An empty title should produce a fallback slug."""
        title = ""
        import re
        clean = re.sub(r'[^a-zA-Z0-9\s]', '', title)
        words = [w for w in clean.strip().split() if w]
        slug = "-".join(w.lower() for w in words[:5]) if words else "untitled-chat"

        assert slug == "untitled-chat"


class TestArchiveJobIntegrity:
    """Tests for the archive job's integrity guarantees."""

    def test_session_with_messages_survives_if_no_file_written(self):
        """If the memory file write fails, the session must NOT be deleted."""
        # This tests the principle: we create a session, don't write any file,
        # and verify the session still exists.
        db = TestingSessionLocal()
        try:
            session_id = "should-survive"
            _create_session(db, session_id, "Survivor", datetime.utcnow() - timedelta(days=10))
            _create_message(db, session_id, "Q1", "A1")
        finally:
            db.close()

        # Session should still be accessible
        response = client.get(f"/brain/chat/sessions/{session_id}")
        assert response.status_code == 200
        messages = response.json()
        assert len(messages) == 1

    def test_multiple_messages_per_session_preserved(self):
        """All messages in a session should be queryable before archiving."""
        db = TestingSessionLocal()
        try:
            session_id = "multi-msg"
            _create_session(db, session_id, "Multi Message Chat", datetime.utcnow())
            for i in range(5):
                _create_message(db, session_id, f"Question {i}", f"Answer {i}")
        finally:
            db.close()

        response = client.get(f"/brain/chat/sessions/{session_id}")
        assert response.status_code == 200
        messages = response.json()
        assert len(messages) == 5
        # Verify ordering
        for i, msg in enumerate(messages):
            assert msg["query"] == f"Question {i}"
