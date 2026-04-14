import pytest
from fastapi.testclient import TestClient
from sqlalchemy import create_engine
from sqlalchemy.orm import sessionmaker
import os

from app.api import app
from app.services.history_db import Base, get_db

# Use a test database
SQLALCHEMY_DATABASE_URL = "sqlite:///./test_history.db"

engine = create_engine(SQLALCHEMY_DATABASE_URL, connect_args={"check_same_thread": False})
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
    if os.path.exists("./test_history.db"):
        os.remove("./test_history.db")

client = TestClient(app)

def test_chat_sessions_flow():
    # 1. Sync a message (this should create a session)
    session_id = "test-session-123"
    payload = {
        "session_id": session_id,
        "query": "Hello Jarvis, how are you?",
        "response": "I am doing well, thank you!",
        "timestamp": "2026-04-14T12:00:00Z"
    }
    response = client.post("/brain/chat/sync", json=payload)
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}

    # 2. Get sessions
    response = client.get("/brain/chat/sessions")
    assert response.status_code == 200
    sessions = response.json()
    assert len(sessions) == 1
    assert sessions[0]["id"] == session_id
    assert sessions[0]["title"] == "Hello Jarvis, how are you?"

    # 3. Get history
    response = client.get(f"/brain/chat/sessions/{session_id}")
    assert response.status_code == 200
    history = response.json()
    assert len(history) == 1
    assert history[0]["query"] == payload["query"]

    # 4. Delete session
    response = client.delete(f"/brain/chat/sessions/{session_id}")
    assert response.status_code == 200
    
    # 5. Verify gone
    response = client.get("/brain/chat/sessions")
    assert len(response.json()) == 0
