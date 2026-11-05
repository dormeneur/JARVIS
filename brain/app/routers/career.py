"""Career copilot helpers for jv-api (14-Career-Copilot.md §4.3): read a resume's text, and the
local-model fallback for turning it into JSON. jv-api owns the prompt, the schema and the checks."""
from __future__ import annotations

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

from app.services.ollama_client import OllamaClient
from app.services.tools import ToolError, safe_path

router = APIRouter(tags=["career"])

RESUME_EXTENSIONS = {".pdf", ".docx", ".md", ".txt"}
LOCAL_MAX_TOKENS = 4096  # a two-page resume as JSON
LOCAL_TIMEOUT = 900.0  # measured 134-517 s on an RTX 3050 shared with other models


class ResumeTextRequest(BaseModel):
    path: str = Field(..., min_length=1, max_length=500)


class ExtractRequest(BaseModel):
    system: str = Field(..., max_length=20_000)
    text: str = Field(..., min_length=1, max_length=60_000)
    schema_: dict = Field(..., alias="schema")


@router.post("/brain/career/resume-text")
async def resume_text(req: ResumeTextRequest, request: Request):
    try:
        path = safe_path(req.path, must_exist=True)
    except ToolError as exc:
        raise HTTPException(status_code=400, detail=str(exc))
    if not path.is_file():
        raise HTTPException(status_code=404, detail="Not a file")
    if path.suffix.lower() not in RESUME_EXTENSIONS:
        raise HTTPException(status_code=400, detail="Use a PDF, Word (.docx), Markdown or text file")
    text = request.app.state.document_loader._extract_content(path) or ""
    return {"text": text}


@router.post("/brain/career/extract-local")
async def extract_local(req: ExtractRequest, request: Request):
    ollama = OllamaClient(request.app.state.embedding_pipeline.ollama_url)
    messages = [
        {"role": "system", "content": req.system},
        {"role": "user", "content": f"Extract the data from this document.\n<document>\n{req.text}\n</document>"},
    ]
    try:
        data = await ollama.chat_json(messages, req.schema_, max_tokens=LOCAL_MAX_TOKENS, timeout=LOCAL_TIMEOUT)
    except Exception as exc:  # noqa: BLE001 - jv-api turns this into a clear 503
        raise HTTPException(status_code=502, detail=f"The local model failed: {exc}")
    return {"data": data if isinstance(data, dict) else {}}
