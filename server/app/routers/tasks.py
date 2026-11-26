"""Tasks across the vault (/tasks) and jobs sent from the phone to the PC (/relay/jobs)."""
from typing import Optional

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, Field

from app.dependencies import get_current_device
from app.services import relay, tasks

router = APIRouter(tags=["tasks"])


class ToggleRequest(BaseModel):
    path: str = Field(min_length=1, max_length=500)
    text: str = Field(min_length=1, max_length=1000)
    done: bool


class AddTaskRequest(BaseModel):
    text: str = Field(min_length=1, max_length=tasks.MAX_TASK * 2)


class JobRequest(BaseModel):
    brief: str = Field(min_length=1, max_length=relay.MAX_BRIEF * 2)


class ReportRequest(BaseModel):
    status: str
    result: Optional[str] = Field(default=None, max_length=relay.MAX_RESULT * 2)


@router.get("/tasks")
async def list_tasks(_device: dict = Depends(get_current_device)):
    return tasks.scan()


@router.post("/tasks/toggle")
async def toggle_task(req: ToggleRequest, _device: dict = Depends(get_current_device)):
    if req.path.split("/")[0].casefold() in ("secrets", "system"):
        raise HTTPException(status_code=403, detail="not a place tasks live")
    if not tasks.toggle(req.path, req.text, req.done):
        raise HTTPException(status_code=409, detail="that task changed elsewhere; reload")
    return {"ok": True}


@router.post("/tasks")
async def add_task(req: AddTaskRequest, _device: dict = Depends(get_current_device)):
    try:
        return {"line": tasks.add(req.text), "path": tasks.INBOX}
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))


@router.post("/relay/jobs", status_code=201)
async def send_job(req: JobRequest, device: dict = Depends(get_current_device)):
    try:
        return relay.create(req.brief, device.get("device_name", "a device"))
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))


@router.get("/relay/jobs")
async def list_jobs(_device: dict = Depends(get_current_device)):
    return {"jobs": relay.recent()}


@router.post("/relay/jobs/claim")
async def claim_job(device: dict = Depends(get_current_device)):
    """For the desktop app: the next job to run, or none."""
    return {"job": relay.claim(device.get("device_name", "a device"))}


@router.post("/relay/jobs/{job_id}")
async def report_job(job_id: str, req: ReportRequest, _device: dict = Depends(get_current_device)):
    try:
        job = relay.report(job_id, req.status, req.result)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
    if job is None:
        raise HTTPException(status_code=404, detail="no such job")
    return job
