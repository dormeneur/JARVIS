"""Career copilot (14-Career-Copilot.md). Admin-only: it reads the owner's resume and spends
their Claude subscription.

Resume extraction: the brain gives the text (it has the PDF/DOCX readers), Claude (jv-jobs, no
tools) turns it into JSON, the local model does when jv-jobs is down, and `resume.check` keeps
only what the text backs up. Nothing is written here: the phone saves the reviewed profile.
"""
import logging

import httpx
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field

from app.config import settings
from app.dependencies import require_admin
from app.services import audit, resume

router = APIRouter(tags=["career"])
logger = logging.getLogger(__name__)

MAX_RESUME_CHARS = 40_000


class ExtractRequest(BaseModel):
    path: str = Field(..., min_length=1, max_length=500)


async def _post(url: str, body: dict, timeout: float, headers: dict | None = None) -> dict:
    async with httpx.AsyncClient(timeout=timeout) as client:
        response = await client.post(url, json=body, headers=headers or {})
        response.raise_for_status()
        return response.json()


async def _resume_text(path: str) -> str:
    try:
        data = await _post(f"{settings.brain_url.rstrip('/')}/brain/career/resume-text", {"path": path}, 60.0)
    except httpx.HTTPStatusError as e:
        code = e.response.status_code
        raise HTTPException(status_code=code if code in (400, 404) else 502, detail="Could not read that file")
    except httpx.RequestError:
        raise HTTPException(status_code=503, detail="AI service offline")
    return str(data.get("text") or "")


async def _extract(text: str) -> tuple[dict, str]:
    """(raw extraction, engine). Claude first (owner's decision 1), the local model as fallback."""
    body = {"system": resume.SYSTEM, "text": text, "schema": resume.SCHEMA}
    if settings.jobs_secret:
        try:
            data = await _post(f"{settings.jobs_url.rstrip('/')}/extract", body, 180.0,
                               headers={"X-Jobs-Secret": settings.jobs_secret})
            return data.get("data") or {}, "claude"
        except (httpx.HTTPError, ValueError) as e:
            logger.warning("Claude resume extraction unavailable, using the local model: %s", type(e).__name__)
    try:
        data = await _post(f"{settings.brain_url.rstrip('/')}/brain/career/extract-local", body, 960.0)
    except httpx.HTTPError:
        raise HTTPException(status_code=503, detail="Neither Claude nor the local model could read the resume")
    return data.get("data") or {}, "local"


@router.post("/career/resume/extract")
async def extract_resume(req: ExtractRequest, device: dict = Depends(require_admin)) -> dict:
    """{profile, counts: {roles, schools, skills}, unverified, engine} for the wizard to review."""
    text = (await _resume_text(req.path)).strip()
    if len(text) < 200:
        raise HTTPException(status_code=422, detail="This resume has no readable text. Upload the Word file or a text PDF.")
    raw, engine = await _extract(text[:MAX_RESUME_CHARS])
    result = resume.check(raw, text)
    # Counts only: never the resume's content.
    audit.record("career.resume_extract", engine=engine, device_id=device.get("device_id"),
                 unverified=len(result["unverified"]), **result["counts"])
    return {**result, "engine": engine}
