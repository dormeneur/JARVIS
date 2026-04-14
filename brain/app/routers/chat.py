from fastapi import APIRouter, Depends, HTTPException
from sqlalchemy.orm import Session
from typing import List
from datetime import datetime
from pydantic import BaseModel

from app.services.history_db import get_db, SessionModel, MessageModel

router = APIRouter(tags=["chat"])

class MessageSync(BaseModel):
    session_id: str
    query: str
    response: str
    timestamp: str  # ISO8601

class SessionResponse(BaseModel):
    id: str
    title: str
    created_at: datetime
    last_active_at: datetime

    class Config:
        from_attributes = True

class MessageResponse(BaseModel):
    id: str
    query: str
    response: str
    timestamp: datetime

    class Config:
        from_attributes = True

@router.get("/brain/chat/sessions", response_model=List[SessionResponse])
async def get_sessions(db: Session = Depends(get_db)):
    """Get all chat sessions ordered by last active time."""
    return db.query(SessionModel).order_by(SessionModel.last_active_at.desc()).all()

@router.get("/brain/chat/sessions/{session_id}", response_model=List[MessageResponse])
async def get_session_history(session_id: str, db: Session = Depends(get_db)):
    """Get full message history for a session."""
    session = db.query(SessionModel).filter(SessionModel.id == session_id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    return db.query(MessageModel).filter(MessageModel.session_id == session_id).order_by(MessageModel.timestamp.asc()).all()

@router.delete("/brain/chat/sessions/{session_id}")
async def delete_session(session_id: str, db: Session = Depends(get_db)):
    """Delete a session and all its messages."""
    session = db.query(SessionModel).filter(SessionModel.id == session_id).first()
    if not session:
        raise HTTPException(status_code=404, detail="Session not found")
    db.delete(session)
    db.commit()
    return {"status": "ok"}

@router.post("/brain/chat/sync")
async def sync_message(msg: MessageSync, db: Session = Depends(get_db)):
    """Sync a new message pair to the brain history."""
    # Ensure session exists
    session = db.query(SessionModel).filter(SessionModel.id == msg.session_id).first()
    if not session:
        # Create session if it doesn't exist (first message)
        # Title is extracted from the message query (first 60 chars)
        title = msg.query[:60]
        session = SessionModel(id=msg.session_id, title=title)
        db.add(session)
    
    # Update last active
    session.last_active_at = datetime.utcnow()
    
    # Add message
    new_msg = MessageModel(
        session_id=msg.session_id,
        query=msg.query,
        response=msg.response,
        timestamp=datetime.fromisoformat(msg.timestamp.replace('Z', '+00:00'))
    )
    db.add(new_msg)
    db.commit()
    return {"status": "ok"}
