"""Agentic chat: the model picks tools itself, the user approves mutations.

Protocol (NDJSON, one JSON object per line):
    {"type":"token","token":"..."}                    prose, incremental
    {"type":"tool_start","id":..,"name":..,"arguments":{..}}
    {"type":"tool_result","id":..,"ok":bool,"summary":".."}
    {"type":"approval_required","id":..,"name":..,"arguments":{..},"risk":".."}
    {"type":"final","answer":"..","sources":[..],"tokens_used":N}
    {"type":"error","error":".."}

Approval is stateless: when a mutating tool is not covered by the grants the
client sent, the stream emits `approval_required` and stops. The client shows
its permission sheet and re-POSTs the same query with the decision appended to
`tool_transcript`, so the loop replays and continues past that point. No
server-side session state to leak or expire.
"""

from __future__ import annotations

import json
import logging
from typing import Any, Dict, List

from fastapi import APIRouter, Request
from fastapi.responses import StreamingResponse

from app.models.ask_models import AgentRequest
from app.services import tools as toolkit
from app.services.ollama_client import LLM_MODEL, OllamaClient
from app.services.retriever import Retriever

router = APIRouter(tags=["agent"])
logger = logging.getLogger(__name__)

# A tool-calling loop that never terminates is the classic agent failure.
MAX_ITERATIONS = 6

AGENT_SYSTEM_PROMPT = """You are JARVIS, a personal assistant for the user's private file vault.

## Using tools
You have tools that act on the real vault. When the user asks you to create, edit,
move, delete, find, or read something, CALL THE TOOL. Never print a shell command
like `touch` or `mv` and never claim you did something you did not do with a tool —
the user cannot run commands from this chat, so a command is useless to them.

Think about which tool fits, call it, then read the result before answering.
If a tool fails, read the error and either fix your arguments and retry once, or
tell the user plainly what went wrong.

## Grounding — this matters more than sounding helpful
Answer from the CONTEXT block and from tool results ONLY. These are the rules:
- If the context and your tools do not contain the answer, say "I don't have that
  in your vault" and stop. Do not guess, and do not fill gaps from general knowledge.
- Never invent file paths, filenames, dates, numbers, or quotes. If you need to know
  what exists, call list_directory or search_vault instead of guessing.
- When you state a fact that came from a vault file, name the file it came from.
- Uncertain is fine. Say "I'm not sure" rather than producing a confident guess.

## Style
Be brief and concrete. No preamble like "I'd be happy to help" — just answer or act.
"""


def _sources_block(sources: List[Any]) -> str:
    if not sources:
        return "No vault context was retrieved for this question."
    parts = []
    for s in sources:
        content = (getattr(s, "content", "") or "").strip()
        if content:
            parts.append(f"[Source: {s.path}]\n{content}")
    return "\n\n".join(parts) if parts else "No vault context was retrieved."


class _SearchServices:
    """Adapter so the search_vault tool can reach the live retriever.

    Also records what the tool surfaced, so those files end up in the
    citation list the UI renders under the answer.
    """

    def __init__(self, retriever: Retriever):
        self._retriever = retriever
        self.hits: List[Dict[str, Any]] = []

    async def search(self, query: str, top_k: int = 5) -> List[Dict[str, Any]]:
        sources = await self._retriever.retrieve(query=query, top_k=top_k)
        out = [
            {"path": s.path, "content": getattr(s, "content", "") or "", "score": s.score}
            for s in sources
        ]
        self.hits.extend(out)
        return out


async def _run_agent(req: AgentRequest, app_state) -> Any:
    embedder = app_state.embedding_pipeline
    store = app_state.vector_store
    client = OllamaClient(embedder.ollama_url)
    retriever = Retriever(embedder, store)

    granted = set(req.granted_tools or [])

    def emit(obj: Dict[str, Any]) -> str:
        return json.dumps(obj) + "\n"

    # --- Seed context with a normal RAG pass so simple questions need no tools.
    try:
        retrieved = await retriever.retrieve(
            query=req.query,
            top_k=5,
            filter_paths=req.attachments or [],
        )
    except Exception as exc:  # noqa: BLE001
        logger.warning("Seed retrieval failed: %s", exc)
        retrieved = []

    messages: List[Dict[str, Any]] = [
        {"role": "system", "content": AGENT_SYSTEM_PROMPT},
        {
            "role": "system",
            "content": (
                f"Current directory: {req.current_directory}\n\n"
                f"=== CONTEXT ===\n{_sources_block(retrieved)}\n=== END CONTEXT ==="
            ),
        },
    ]
    for turn in (req.chat_history or [])[-10:]:
        messages.append({"role": turn.role, "content": turn.content})
    messages.append({"role": "user", "content": req.query})

    # --- Replay any tool calls already decided in this turn.
    for entry in req.tool_transcript or []:
        messages.append(
            {
                "role": "assistant",
                "content": "",
                "tool_calls": [
                    {"type": "function", "function": {"name": entry.name, "arguments": entry.arguments}}
                ],
            }
        )
        if entry.approved:
            try:
                result = await toolkit.execute(
                    entry.name, entry.arguments, _SearchServices(retriever)
                )
            except toolkit.ToolError as exc:
                result = f"Error: {exc}"
        else:
            result = "The user denied permission for this action. Do not retry it; explain and offer an alternative."
        messages.append({"role": "tool", "tool_name": entry.name, "content": result})

    extra_sources: List[Dict[str, Any]] = []
    answer_parts: List[str] = []
    tokens_used = 0
    call_seq = len(req.tool_transcript or [])

    for _ in range(MAX_ITERATIONS):
        pending_calls: List[Dict[str, Any]] = []
        turn_text = ""

        async for event in client.chat_with_tools(messages, toolkit.tool_schemas()):
            if "error" in event:
                yield emit({"type": "error", "error": event["error"]})
                return
            if "token" in event:
                turn_text += event["token"]
                answer_parts.append(event["token"])
                yield emit({"type": "token", "token": event["token"]})
            elif "tool_calls" in event:
                pending_calls = event["tool_calls"]
            elif "done" in event:
                tokens_used += event["done"].get("eval_count", 0)

        if not pending_calls:
            break

        messages.append(
            {
                "role": "assistant",
                "content": turn_text,
                "tool_calls": [
                    {"type": "function", "function": {"name": c["name"], "arguments": c["arguments"]}}
                    for c in pending_calls
                ],
            }
        )

        for call in pending_calls:
            name = call.get("name", "")
            args = call.get("arguments") or {}
            call_seq += 1
            call_id = f"call-{call_seq}"
            risk = toolkit.risk_of(name)

            # Mutations stop the stream until the client sends a decision back.
            if risk != toolkit.RISK_READ and name not in granted:
                yield emit(
                    {
                        "type": "approval_required",
                        "id": call_id,
                        "name": name,
                        "arguments": args,
                        "risk": risk,
                    }
                )
                return

            yield emit({"type": "tool_start", "id": call_id, "name": name, "arguments": args})

            services = _SearchServices(retriever)
            try:
                result = await toolkit.execute(name, args, services)
                ok = True
            except toolkit.ToolError as exc:
                result = f"Error: {exc}"
                ok = False

            # Anything search_vault surfaced becomes a citation too.
            extra_sources.extend(
                {"path": h["path"], "chunk": 0, "score": h.get("score", 0.0)}
                for h in services.hits
            )

            yield emit(
                {
                    "type": "tool_result",
                    "id": call_id,
                    "name": name,
                    "ok": ok,
                    "summary": result[:400],
                }
            )
            messages.append({"role": "tool", "tool_name": name, "content": result})
    else:
        yield emit(
            {
                "type": "token",
                "token": "\n\n(Stopped after too many tool steps — please narrow the request.)",
            }
        )

    all_sources = [
        {"path": s.path, "chunk": s.chunk, "score": s.score} for s in retrieved
    ] + extra_sources

    seen = set()
    unique_sources = []
    for s in all_sources:
        if s["path"] in seen:
            continue
        seen.add(s["path"])
        unique_sources.append(s)

    yield emit(
        {
            "type": "final",
            "answer": "".join(answer_parts),
            "sources": unique_sources,
            "model": LLM_MODEL,
            "tokens_used": tokens_used,
        }
    )


@router.post("/brain/ai/agent")
async def agent_chat(req: AgentRequest, request: Request):
    """Agentic chat with native tool calling and client-side approval."""
    return StreamingResponse(
        _run_agent(req, request.app.state),
        media_type="application/x-ndjson",
    )


@router.get("/brain/ai/tools")
async def list_tools():
    """Expose the tool catalogue so the client can render permission UI."""
    return {
        "model": LLM_MODEL,
        "tools": [
            {
                "name": t.name,
                "description": t.description,
                "risk": t.risk,
                "requires_approval": t.requires_approval,
            }
            for t in toolkit.TOOLS.values()
        ],
    }
