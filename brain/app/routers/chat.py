from fastapi import APIRouter, HTTPException
from typing import List
from pydantic import BaseModel

from app.services.history_db import (
    get_db,
    get_all_sessions,
    get_session,
    get_messages,
    upsert_session,
    add_message,
    delete_session as _delete_session,
    SessionModel,
    MessageModel,
)

router = APIRouter(tags=["chat"])


class MessageSync(BaseModel):
    session_id: str
    query: str
    response: str
    timestamp: str  # ISO8601


class SessionResponse(BaseModel):
    id: str
    title: str
    created_at: str
    last_active_at: str

    @classmethod
    def from_model(cls, m: SessionModel) -> "SessionResponse":
        return cls(
            id=m.id,
            title=m.title,
            created_at=m.created_at.isoformat(),
            last_active_at=m.last_active_at.isoformat(),
        )


class MessageResponse(BaseModel):
    id: str
    query: str
    response: str
    timestamp: str

    @classmethod
    def from_model(cls, m: MessageModel) -> "MessageResponse":
        return cls(
            id=m.id,
            query=m.query,
            response=m.response,
            timestamp=m.timestamp.isoformat(),
        )


@router.get("/brain/chat/sessions", response_model=List[SessionResponse])
async def get_sessions():
    """Get all chat sessions ordered by last active time."""
    with get_db() as conn:
        return [SessionResponse.from_model(s) for s in get_all_sessions(conn)]


@router.get("/brain/chat/sessions/{session_id}", response_model=List[MessageResponse])
async def get_session_history(session_id: str):
    """Get full message history for a session."""
    with get_db() as conn:
        session = get_session(conn, session_id)
        if not session:
            raise HTTPException(status_code=404, detail="Session not found")
        return [MessageResponse.from_model(m) for m in get_messages(conn, session_id)]


@router.delete("/brain/chat/sessions/{session_id}")
async def delete_session_endpoint(session_id: str):
    """Delete a session and all its messages."""
    with get_db() as conn:
        session = get_session(conn, session_id)
        if not session:
            raise HTTPException(status_code=404, detail="Session not found")
        _delete_session(conn, session_id)
    return {"status": "ok"}


@router.post("/brain/chat/sync")
async def sync_message(msg: MessageSync):
    """Sync a new message pair to the brain history."""
    with get_db() as conn:
        # Title = first 60 chars of the query (only used on first message)
        upsert_session(conn, msg.session_id, msg.query[:60])
        add_message(conn, msg.session_id, msg.query, msg.response, msg.timestamp)
    return {"status": "ok"}
