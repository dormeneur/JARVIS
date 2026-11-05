"""Manual trigger for the daily digest (the scheduler runs it on its own at JARVIS_DIGEST_TIME)."""
from __future__ import annotations

from fastapi import APIRouter, Request

from app.services import digest

router = APIRouter(tags=["digest"])


@router.post("/brain/digest/run")
async def run_digest(request: Request, force: bool = False):
    path = await digest.run_digest(request.app, force=force)
    return {"written": path}
