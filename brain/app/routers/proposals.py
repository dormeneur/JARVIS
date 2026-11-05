"""Proposal inbox endpoints (see services/proposals.py)."""
from __future__ import annotations

from typing import Any, Dict

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from app.services import proposals

router = APIRouter(tags=["proposals"])


class Decision(BaseModel):
    approve: bool


@router.get("/brain/proposals")
async def list_proposals(status: str = proposals.PENDING, limit: int = 50) -> Dict[str, Any]:
    return {"proposals": proposals.list_proposals(status, min(max(limit, 1), 200))}


@router.get("/brain/proposals/count")
async def count_proposals() -> Dict[str, int]:
    return {"pending": proposals.count_pending()}


@router.post("/brain/proposals/{proposal_id}/decision")
async def decide_proposal(proposal_id: str, body: Decision) -> Dict[str, Any]:
    try:
        return await proposals.decide(proposal_id, body.approve)
    except KeyError:
        raise HTTPException(status_code=404, detail="No such proposal")
