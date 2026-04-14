"""Proxy router for sending requests to the JARVIS AI Brain."""

import json
import logging
from typing import AsyncGenerator

import httpx
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import StreamingResponse

from app.config import settings
from app.dependencies import get_current_device
from app.services import auth as auth_service

router = APIRouter(tags=["ask"])
logger = logging.getLogger(__name__)


async def proxy_stream(payload: dict, url: str) -> AsyncGenerator[bytes, None]:
    """Proxy a streaming request to the brain service — raw byte forwarding."""
    client = httpx.AsyncClient(timeout=120.0)
    try:
        logger.info(f"Opening proxy stream to: {url} with payload: {payload}")
        async with client.stream("POST", url, json=payload) as response:
            if response.status_code != 200:
                await response.aread()
                error_msg = json.dumps({"error": f"Brain returned {response.status_code}: {response.text}"}) + "\n"
                logger.error(f"Brain error: {response.status_code}")
                yield error_msg.encode("utf-8")
                return
                
            logger.info("Connected to brain stream. Forwarding raw bytes...")
            async for chunk in response.aiter_bytes():
                yield chunk
            logger.info("Proxy stream completed.")
    except httpx.RequestError as e:
        logger.error(f"Failed to connect to jv-brain: {e}")
        error_msg = json.dumps({"error": f"AI service unavailable: {str(e)}"}) + "\n"
        yield error_msg.encode("utf-8")
    finally:
        await client.aclose()


@router.post("/ask/ai/query")
async def ask_jarvis(
    request: Request,
    device=Depends(get_current_device),
):
    """Proxy questions to the JARVIS Brain service (Streaming)."""
    brain_ask_url = f"{settings.brain_url.rstrip('/')}/brain/ai/query"
    
    # Pre-read the body BEFORE creating the generator
    # StreamingResponse runs generators lazily, so req.json() would fail inside one
    try:
        payload = await request.json()
    except Exception:
        payload = {}
    
    logger.info(f"[ASK] Received request, forwarding to {brain_ask_url}")
    
    return StreamingResponse(
        proxy_stream(payload, brain_ask_url),
        media_type="application/x-ndjson"
    )

@router.get("/ask/status")
async def ask_status(device=Depends(get_current_device)):
    """Check health of AI subsystem."""
    url = f"{settings.brain_url.rstrip('/')}/brain/status"
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.get(url)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.warning(f"Failed to check brain status: {e}")
        return {
            "status": "unreachable",
            "ollama": "unknown",
            "chromadb": "unknown",
            "indexing": False
        }

@router.get("/ask/index-status")
async def ask_index_status(device=Depends(get_current_device)):
    """Check indexing status of AI subsystem."""
    url = f"{settings.brain_url.rstrip('/')}/brain/index-status"
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.get(url)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.error(f"Failed to get indexing status: {e}")
        raise HTTPException(status_code=503, detail="AI indexing service offline")

@router.post("/ask/reindex")
async def ask_reindex(device=Depends(get_current_device)):
    """Trigger AI subsystem re-indexing."""
    url = f"{settings.brain_url.rstrip('/')}/brain/reindex"
    try:
        async with httpx.AsyncClient(timeout=5.0) as client:
            response = await client.post(url)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.error(f"Failed to trigger re-indexing: {e}")
        raise HTTPException(status_code=503, detail="AI indexing service offline")

@router.post("/ask/extract-pdf")
async def ask_extract_pdf(request: Request, device=Depends(get_current_device)):
    """Extract specific pages of a PDF via AI Brain."""
    url = f"{settings.brain_url.rstrip('/')}/brain/extract-pdf"
    try:
        payload = await request.json()
        async with httpx.AsyncClient(timeout=30.0) as client:
            response = await client.post(url, json=payload)
            response.raise_for_status()
            return response.json()
    except httpx.HTTPStatusError as e:
        raise HTTPException(status_code=e.response.status_code, detail=e.response.text)
    except Exception as e:
        logger.error(f"Failed to extract PDF via brain: {e}")
        raise HTTPException(status_code=503, detail="AI indexing service offline")

@router.post("/ask/generate-files")
async def ask_generate_files(request: Request, device=Depends(get_current_device)):
    """Generate files via AI Brain."""
    url = f"{settings.brain_url.rstrip('/')}/brain/generate-files"
    try:
        payload = await request.json()
        async with httpx.AsyncClient(timeout=120.0) as client:
            response = await client.post(url, json=payload)
            response.raise_for_status()
            return response.json()
    except httpx.HTTPStatusError as e:
        raise HTTPException(status_code=e.response.status_code, detail=e.response.text)
    except Exception as e:
        logger.error(f"Failed to generate files via brain: {e}")
        raise HTTPException(status_code=503, detail="AI service offline")

@router.post("/ask/generate-files/dry-run")
async def ask_generate_files_dry_run(request: Request, device=Depends(get_current_device)):
    """Generate files via AI Brain without writing."""
    url = f"{settings.brain_url.rstrip('/')}/brain/generate-files/dry-run"
    try:
        payload = await request.json()
        async with httpx.AsyncClient(timeout=120.0) as client:
            response = await client.post(url, json=payload)
            response.raise_for_status()
            return response.json()
    except httpx.HTTPStatusError as e:
        raise HTTPException(status_code=e.response.status_code, detail=e.response.text)
    except Exception as e:
        logger.error(f"Failed to generate files via brain dry-run: {e}")
        raise HTTPException(status_code=503, detail="AI service offline")

@router.post("/ask/refresh-fs-tree")
async def ask_refresh_fs_tree(device=Depends(get_current_device)):
    """Trigger filesystem tree snapshot rebuild in Brain."""
    url = f"{settings.brain_url.rstrip('/')}/brain/refresh-fs-tree"
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.post(url)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.error(f"Failed to refresh fs-tree via brain: {e}")
        raise HTTPException(status_code=503, detail="AI service offline")

@router.get("/ask/chat/sessions")
async def ask_get_sessions(device=Depends(get_current_device)):
    """Get all chat sessions via brain."""
    url = f"{settings.brain_url.rstrip('/')}/brain/chat/sessions"
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.get(url)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.error(f"Failed to fetch sessions from brain: {e}")
        raise HTTPException(status_code=503, detail="AI service offline")

@router.get("/ask/chat/sessions/{session_id}")
async def ask_get_session_history(session_id: str, device=Depends(get_current_device)):
    """Get full message history for a session via brain."""
    url = f"{settings.brain_url.rstrip('/')}/brain/chat/sessions/{session_id}"
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.get(url)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.error(f"Failed to fetch history from brain: {e}")
        raise HTTPException(status_code=503, detail="AI service offline")

@router.delete("/ask/chat/sessions/{session_id}")
async def ask_delete_session(session_id: str, device=Depends(get_current_device)):
    """Delete a session via brain."""
    url = f"{settings.brain_url.rstrip('/')}/brain/chat/sessions/{session_id}"
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.delete(url)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.error(f"Failed to delete session in brain: {e}")
        raise HTTPException(status_code=503, detail="AI service offline")

@router.post("/ask/chat/sync")
async def ask_sync_message(msg: dict, device=Depends(get_current_device)):
    """Sync message pair to brain history."""
    url = f"{settings.brain_url.rstrip('/')}/brain/chat/sync"
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.post(url, json=msg)
            response.raise_for_status()
            return response.json()
    except Exception as e:
        logger.error(f"Failed to sync message to brain: {e}")
        raise HTTPException(status_code=503, detail="AI service offline")
