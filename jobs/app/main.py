"""jv-jobs: runs the ai-job-search engine for JARVIS. Internal-only; jv-api is
the sole caller and does the admin check (12-Job-Search-Integration.md §3.9)."""
from __future__ import annotations

import hmac
import json
import logging
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Header, HTTPException
from fastapi.responses import FileResponse, StreamingResponse
from pydantic import BaseModel, Field

from app import board
from app.config import settings
from app.runner import KINDS, claude_auth_status, extract, runner

logging.basicConfig(level=logging.INFO)


@asynccontextmanager
async def lifespan(_app: FastAPI):
    runner.load()
    runner.schedule()
    yield


def check_secret(x_jobs_secret: str = Header(default="")) -> None:
    if not settings.jobs_secret or not hmac.compare_digest(x_jobs_secret, settings.jobs_secret):
        raise HTTPException(status_code=401, detail="bad jobs secret")


app = FastAPI(title="jv-jobs", lifespan=lifespan, dependencies=[Depends(check_secret)])


class RunRequest(BaseModel):
    kind: str
    args: dict = Field(default_factory=dict)


class ExtractRequest(BaseModel):
    system: str = Field(..., max_length=20_000)
    text: str = Field(..., min_length=1, max_length=60_000)
    schema_: dict = Field(..., alias="schema")


class AnswerRequest(BaseModel):
    text: str


def _public(run: dict) -> dict:
    return {k: v for k, v in run.items() if k != "pending_prompt"}


def _get(run_id: str) -> dict:
    if run_id not in runner.runs:
        raise HTTPException(status_code=404, detail="run not found")
    return runner.runs[run_id]


@app.get("/status")
async def status() -> dict:
    runs = list(runner.runs.values())
    return {
        "claude": await claude_auth_status(),
        "workspace_ok": (settings.workspace / "CLAUDE.md").is_file(),
        "running": sum(r["status"] == "running" for r in runs),
        "queued": sum(r["status"] == "queued" for r in runs),
        "awaiting_input": sum(r["status"] == "awaiting_input" for r in runs),
        "tokens_total": sum(r["usage"]["input_tokens"] + r["usage"]["output_tokens"] for r in runs),
        "kinds": sorted(KINDS),
    }


@app.post("/extract")
async def extract_data(body: ExtractRequest) -> dict:
    try:
        return await extract(body.system, body.text, body.schema_)
    except (OSError, RuntimeError) as e:
        raise HTTPException(status_code=502, detail=str(e))


@app.get("/board")
async def get_board() -> dict:
    return board.build_board(list(runner.runs.values()))


@app.get("/runs")
async def list_runs(limit: int = 50) -> dict:
    runs = sorted(runner.runs.values(), key=lambda r: r["created"], reverse=True)[:limit]
    return {"runs": [_public(r) for r in runs]}


@app.post("/runs", status_code=201)
async def create_run(body: RunRequest) -> dict:
    try:
        return _public(await runner.submit(body.kind, body.args))
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.get("/runs/{run_id}")
async def get_run(run_id: str) -> dict:
    return _public(_get(run_id))


@app.get("/runs/{run_id}/events")
async def run_events(run_id: str) -> StreamingResponse:
    _get(run_id)

    async def gen():
        async for event in runner.follow(run_id):
            yield json.dumps(event) + "\n"

    return StreamingResponse(gen(), media_type="application/x-ndjson")


async def _act(run_id: str, action) -> dict:
    # Callers check the run exists first, so `action` is never left unawaited.
    try:
        return _public(await action)
    except ValueError as e:
        raise HTTPException(status_code=409, detail=str(e))


@app.post("/runs/{run_id}/answer")
async def answer(run_id: str, body: AnswerRequest) -> dict:
    _get(run_id)
    return await _act(run_id, runner.answer(run_id, body.text))


@app.post("/runs/{run_id}/retry")
async def retry(run_id: str) -> dict:
    _get(run_id)
    return await _act(run_id, runner.retry(run_id))


@app.post("/runs/{run_id}/cancel")
async def cancel(run_id: str) -> dict:
    _get(run_id)
    return await _act(run_id, runner.cancel(run_id))


@app.post("/runs/{run_id}/restore")
async def restore(run_id: str) -> dict:
    _get(run_id)
    try:
        return {"restored": runner.restore(run_id)}
    except ValueError as e:
        raise HTTPException(status_code=409, detail=str(e))


def _artifact(path: str):
    try:
        return board.artifact_path(path)
    except FileNotFoundError:
        raise HTTPException(status_code=404, detail="artifact not found")
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.get("/artifact")
async def artifact(path: str) -> FileResponse:
    target = _artifact(path)
    return FileResponse(target, filename=target.name)


@app.get("/artifact/text")
async def artifact_text(path: str) -> dict:
    target = _artifact(path)
    return {"path": path, "text": board.artifact_text(target)}
