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


@router.post("/ask")
async def ask_jarvis(
    request: Request,
    device=Depends(get_current_device),
):
    """Proxy questions to the JARVIS Brain service (Streaming)."""
    brain_ask_url = f"{settings.brain_url.rstrip('/')}/brain/ask"
    
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
