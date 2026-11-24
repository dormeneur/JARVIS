"""Hunches API (see services/hunches.py). Local model only; jv-api proxies these as /ask/hunches/*."""
from typing import List, Optional

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, Field

from app.services import hunches

router = APIRouter(tags=["hunches"])


class Sample(BaseModel):
    at: Optional[str] = None
    app: str
    title: str = ""
    seconds: int = Field(ge=1, le=3600)


class ObserveRequest(BaseModel):
    samples: List[Sample] = Field(default_factory=list, max_length=500)


class NextRequest(BaseModel):
    session: str = Field(min_length=1, max_length=64)
    on_demand: bool = False


class AnswerRequest(BaseModel):
    choice: Optional[int] = Field(default=None, ge=0, le=2)
    text: Optional[str] = Field(default=None, max_length=500)


class SkipRequest(BaseModel):
    session: str = Field(min_length=1, max_length=64)


@router.post("/brain/hunches/observe")
async def observe(req: ObserveRequest):
    return {"stored": hunches.record_observations(s.model_dump() for s in req.samples)}


@router.post("/brain/hunches/next")
async def next_hunch(req: NextRequest):
    return hunches.next_hunch(req.session, req.on_demand)


@router.post("/brain/hunches/{hunch_id}/answer")
async def answer(hunch_id: str, req: AnswerRequest):
    try:
        return hunches.answer(hunch_id, req.choice, req.text)
    except KeyError:
        raise HTTPException(status_code=404, detail="no such hunch")
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))


@router.post("/brain/hunches/{hunch_id}/skip")
async def skip(hunch_id: str, req: SkipRequest):
    return {"stacked": hunches.skip(hunch_id, req.session)}


@router.post("/brain/hunches/generate")
async def generate():
    return {"created": await hunches.generate()}


@router.get("/brain/hunches/status")
async def status():
    return hunches.status()


class PlaceRequest(BaseModel):
    fact: str = Field(min_length=1, max_length=500)


@router.post("/brain/profile/place")
async def place_fact(req: PlaceRequest):
    """Which Profile file a new fact about the user belongs in (local model; may name a new file or folder)."""
    from app.services import profile_place

    try:
        return {"path": await profile_place.place(req.fact)}
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))


class IngestRequest(BaseModel):
    text: str = Field(min_length=1, max_length=40000)
    source: str = Field(default="", max_length=300)
    url: Optional[str] = Field(default=None, max_length=2000)


@router.post("/brain/ingest/plan")
async def ingest_plan(req: IngestRequest):
    """Where outside text belongs in the vault (a note, plus facts about the owner). Writes nothing."""
    from app.services import ingest

    try:
        return await ingest.plan(req.text, req.source, req.url)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
