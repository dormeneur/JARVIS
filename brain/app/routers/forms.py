"""Form filling: answer a web form's questions from the vault, on this machine only.

The desktop app reads the questions off the page; this answers each from Profile/ (what
interview mode recorded, standard-answers.md included) plus the closest passages elsewhere in
the vault. An answer survives only if it cites a file the model was actually given and, for
choice questions, picks offered options, so the user is never handed a guess to copy.
"""

from __future__ import annotations

import json
import logging
from typing import List, Literal, Optional

from fastapi import APIRouter, Request
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field

from app.services.ollama_client import OllamaClient
from app.services.retriever import Retriever
from app.services.tools import ToolError, safe_path

router = APIRouter(tags=["forms"])
logger = logging.getLogger(__name__)

PROFILE_DIR = "Profile"
BATCH_SIZE = 6
PROFILE_BUDGET_CHARS = 12000  # ~3k tokens: interview-mode profiles are small
EXTRA_BUDGET_CHARS = 6000  # the rest of the vault, per batch
EXTRA_PER_QUESTION = 2
ATTACHMENT_BUDGET_CHARS = 8000  # per attached file (a resume, the form's own context files)
CHOICE_KINDS = {"choice", "dropdown", "checkbox"}
NOT_AN_ANSWER = {"", "null", "none", "unknown", "n/a", "not stated", "not provided"}

SYSTEM_PROMPT = """You fill in a web form for the user, using ONLY facts stated in the vault files below.
For each question give the value the user would put in the form: just the value, no explanation.
If the files do not state it, set "answer" to "" (empty). Never guess, infer or invent.
For questions with options, copy one option exactly; for checkbox questions you may list
several options separated by "; ".
Set "source" to the exact file name (as shown after "File:") the answer came from, and
"evidence" to the exact words from that file that state it (copied, at most one line).
For an option, only pick it when the evidence plainly states that fact.
Set "open" to true when the question asks for the user's motivation, opinion or a written
pitch (e.g. "Why do you want to work here?", "Why should we hire you?"), otherwise false."""

COMPOSE_PROMPT = """You write the user's answer to one question on an application form, in the first
person, as they would write it: 60 to 120 words, warm and specific, plain text with no heading.
Use only facts about the user from the vault files and facts about the organisation from the
research notes. Never invent experience, numbers, names or achievements. If there are no research
notes, don't claim specifics about the organisation."""

ANSWER_SCHEMA = {
    "type": "object",
    "properties": {
        "answers": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "id": {"type": "string"},
                    "answer": {"type": "string"},
                    "source": {"type": "string"},
                    "evidence": {"type": "string"},
                    "open": {"type": "boolean"},
                },
                "required": ["id", "answer", "source", "evidence", "open"],
            },
        }
    },
    "required": ["answers"],
}


class FormQuestion(BaseModel):
    id: str = Field(..., max_length=64)
    text: str = Field(..., max_length=1000)
    kind: Literal["text", "paragraph", "choice", "checkbox", "dropdown", "date", "time", "other"] = "text"
    options: List[str] = Field(default_factory=list, max_length=100)
    required: bool = False


class FormAnswersRequest(BaseModel):
    title: str = Field(default="", max_length=300)
    questions: List[FormQuestion] = Field(..., max_length=80)
    attachments: List[str] = Field(default_factory=list, max_length=10, description="Vault files to read in full, e.g. the resume")


def load_profile() -> list[tuple[str, str]]:
    """Profile/*.md as (vault path, text), standard answers first, within the budget."""
    try:
        folder = safe_path(PROFILE_DIR)
    except ToolError:
        return []
    if not folder.is_dir():
        return []

    files = sorted(folder.glob("*.md"), key=lambda p: (p.name != "standard-answers.md", p.name))
    loaded, used = [], 0
    for path in files:
        text = path.read_text(encoding="utf-8", errors="replace").strip()
        if not text:
            continue
        text = text[: max(0, PROFILE_BUDGET_CHARS - used)]
        if not text:
            break
        loaded.append((f"{PROFILE_DIR}/{path.name}", text))
        used += len(text)
    return loaded


def _squash(text: str) -> str:
    return " ".join(text.casefold().replace("*", "").split())


def accept(question: FormQuestion, answer: str, source: str, evidence: str, given: dict[str, str]) -> Optional[tuple[str, str]]:
    """The answer and its file, or None unless the cited file was given and really contains the
    quoted evidence, and (for choices) the answer is offered options."""
    answer = (answer or "").strip()
    if answer.casefold().rstrip(".") in NOT_AN_ANSWER:
        return None

    source = (source or "").strip().replace("\\", "/")
    cited = next((path for path in given if path == source or path.rsplit("/", 1)[-1] == source), None)
    quote = _squash(evidence or "")
    if cited is None or len(quote) < 2 or quote not in _squash(given[cited]):
        return None

    if question.kind in CHOICE_KINDS and question.options:
        by_fold = {option.casefold(): option for option in question.options}
        parts = [part.strip() for part in answer.split(";")] if question.kind == "checkbox" else [answer]
        picked = [by_fold.get(part.casefold()) for part in parts if part]
        if not picked or any(option is None for option in picked):
            return None
        answer = "; ".join(picked)
    return answer, cited


def load_attachments(app_state, paths: List[str]) -> list[tuple[str, str]]:
    """The user's chosen files (resume, context for this form), text extracted, Secrets refused."""
    loaded = []
    for raw in paths:
        try:
            path = safe_path(raw, must_exist=True)
        except ToolError as exc:
            logger.warning("Form attachment refused: %s", exc)
            continue
        text = (app_state.document_loader._extract_content(path) or "").strip()
        if text:
            loaded.append((raw.replace("\\", "/").lstrip("/"), text[:ATTACHMENT_BUDGET_CHARS]))
    return loaded


async def _extra_context(retriever: Retriever, questions: List[FormQuestion]) -> list[tuple[str, str]]:
    """The closest non-Profile passage per question (Secrets are never indexed)."""
    found: dict[str, str] = {}
    used = 0
    for question in questions:
        try:
            sources = await retriever.retrieve(query=question.text, top_k=EXTRA_PER_QUESTION)
        except Exception as exc:  # noqa: BLE001 - an empty index just means Profile only
            logger.warning("Form retrieval failed: %s", exc)
            return list(found.items())
        for source in sources:
            content = (source.content or "").strip()
            if not content or source.path in found or source.path.startswith(f"{PROFILE_DIR}/"):
                continue
            try:
                safe_path(source.path)  # defence in depth: an excluded folder never reaches the model
            except ToolError:
                continue
            if used + len(content) > EXTRA_BUDGET_CHARS:
                return list(found.items())
            found[source.path] = content
            used += len(content)
    return list(found.items())


def build_messages(title: str, questions: List[FormQuestion], files: list[tuple[str, str]]) -> list[dict]:
    vault = "\n\n".join(f"File: {path}\n{text}" for path, text in files) or "(no vault files)"
    asked = [
        {"id": q.id, "question": q.text, **({"options": q.options} if q.options else {}), "kind": q.kind}
        for q in questions
    ]
    user = (
        f"=== VAULT FILES ===\n{vault}\n=== END VAULT FILES ===\n\n"
        f"Form: {title or '(untitled)'}\nQuestions (JSON):\n{json.dumps(asked, ensure_ascii=False)}"
    )
    return [{"role": "system", "content": SYSTEM_PROMPT}, {"role": "user", "content": user}]


async def answer_stream(req: FormAnswersRequest, app_state):
    ollama = OllamaClient(app_state.embedding_pipeline.ollama_url)
    retriever = Retriever(app_state.embedding_pipeline, app_state.vector_store)
    profile = load_profile() + load_attachments(app_state, req.attachments)

    for start in range(0, len(req.questions), BATCH_SIZE):
        batch = req.questions[start : start + BATCH_SIZE]
        # A batch can take a minute on a small GPU: say so, which also keeps the proxy's read alive.
        yield json.dumps({"working": start}) + "\n"
        files = profile + await _extra_context(retriever, batch)
        given = dict(files)
        replies: dict[str, dict] = {}
        try:
            reply = await ollama.chat_json(build_messages(req.title, batch, files), ANSWER_SCHEMA)
            replies = {str(item.get("id")): item for item in reply.get("answers", []) if isinstance(item, dict)}
        except Exception as exc:  # noqa: BLE001 - one bad batch marks its questions unknown
            logger.error("Form batch failed: %s", exc)
            yield json.dumps({"error": f"The local model failed on some questions: {exc}"}) + "\n"

        for question in batch:
            item = replies.get(question.id, {})
            kept = accept(question, *(str(item.get(key, "")) for key in ("answer", "source", "evidence")), given)
            yield json.dumps({
                "id": question.id,
                "answer": kept[0] if kept else None,
                "source": kept[1] if kept else None,
                "open": bool(item.get("open")),
            }, ensure_ascii=False) + "\n"

    yield json.dumps({"done": True}) + "\n"


class ComposeRequest(BaseModel):
    question: str = Field(..., max_length=1000)
    title: str = Field(default="", max_length=300)
    research: str = Field(default="", max_length=8000)
    attachments: List[str] = Field(default_factory=list, max_length=10)


async def compose_stream(req: ComposeRequest, app_state):
    ollama = OllamaClient(app_state.embedding_pipeline.ollama_url)
    retriever = Retriever(app_state.embedding_pipeline, app_state.vector_store)
    yield json.dumps({"working": 0}) + "\n"
    files = load_profile() + load_attachments(app_state, req.attachments) + await _extra_context(retriever, [FormQuestion(id="c", text=req.question)])
    vault = "\n\n".join(f"File: {path}\n{text}" for path, text in files) or "(no vault files)"
    user = (
        f"=== VAULT FILES ===\n{vault}\n=== END VAULT FILES ===\n\n"
        f"=== RESEARCH NOTES ===\n{req.research.strip() or '(none)'}\n=== END RESEARCH NOTES ===\n\n"
        f"Form: {req.title or '(untitled)'}\nQuestion: {req.question}"
    )
    messages = [{"role": "system", "content": COMPOSE_PROMPT}, {"role": "user", "content": user}]
    async for event in ollama.chat_with_tools(messages, think=False):
        if "token" in event:
            yield json.dumps({"token": event["token"]}, ensure_ascii=False) + "\n"
        elif "error" in event:
            yield json.dumps({"error": event["error"]}) + "\n"
            return
    yield json.dumps({"done": True, "sources": [path for path, _ in files]}) + "\n"


@router.post("/brain/ai/compose-answer")
async def compose_answer(req: ComposeRequest, request: Request):
    """NDJSON tokens of a drafted answer to an open question, from the vault (and public research)."""
    return StreamingResponse(compose_stream(req, request.app.state), media_type="application/x-ndjson")


@router.post("/brain/ai/form-answers")
async def form_answers(req: FormAnswersRequest, request: Request):
    """NDJSON: one {id, answer|null, source|null} line per question as each batch finishes, then {done}."""
    return StreamingResponse(answer_stream(req, request.app.state), media_type="application/x-ndjson")
