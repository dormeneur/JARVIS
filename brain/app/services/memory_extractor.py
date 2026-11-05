"""Learn durable facts about the user from what they say in chat, as *proposals*.

After a chat turn, the local model reads the USER's message (never the assistant's reply: a small
model hallucinates there) and lists stable facts: identity, work, goals, preferences, people.
Those become proposals to append to Profile/<topic>.md; nothing is written until the user approves
it in the Inbox. Runs in the background after the answer is out, one at a time (a busy extractor
skips rather than queueing), and never raises.
"""
from __future__ import annotations

import asyncio
import logging
import re
import time
from typing import Any, Dict, List, Optional, Set

from app.config import settings
from app.services import proposals, tools as toolkit
from app.services.ollama_client import LLM_MODEL, OllamaClient
from app.services.traces import record

logger = logging.getLogger(__name__)

TOPICS = ["about", "work", "education", "skills", "goals", "preferences", "people"]
MIN_CHARS = 25
MAX_FACTS = 5
MAX_FACT_CHARS = 200

SYSTEM_PROMPT = (
    "From the USER's message only, list durable facts about the user that will still be true in "
    "months: identity, work, education, skills, goals, preferences, important people. Questions, "
    "requests and one-off task details are not facts. Use the user's own words. "
    "If there is nothing, return an empty list."
)

SCHEMA = {
    "type": "object",
    "properties": {
        "facts": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {"topic": {"type": "string", "enum": TOPICS}, "fact": {"type": "string"}},
                "required": ["topic", "fact"],
            },
        }
    },
    "required": ["facts"],
}

# Text that must never even reach the model, let alone a Profile file.
_SECRET = re.compile(
    r"(password|passcode|passwd|\bpin\b|\botp\b)\s*[:=]|sk-[A-Za-z0-9]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b\d{12,19}\b",
    re.IGNORECASE,
)

_busy = asyncio.Lock()
_tasks: Set[asyncio.Task] = set()

# The phone prefixes a reply to an earlier message with that message quoted in triple quotes. If it
# quotes JARVIS's answer, that is assistant text, which must never be mined for facts.
_QUOTED = re.compile(r'Regarding this earlier [^\n]*:\n"""\n.*?\n"""\n\n', re.DOTALL)


def own_words(text: str) -> str:
    """The user's message without any quoted earlier message."""
    return _QUOTED.sub("", text or "").strip()


def _norm(text: str) -> str:
    return re.sub(r"\s+", " ", text.casefold()).strip()


def _already_known(fact: str, topic: str) -> bool:
    needle = _norm(fact)
    try:
        profile = toolkit.safe_path(f"Profile/{topic}.md")
        if profile.is_file() and needle in _norm(profile.read_text(encoding="utf-8", errors="replace")):
            return True
    except toolkit.ToolError:
        pass
    return any(needle in _norm(p["detail"]) for p in proposals.list_proposals(limit=200))


def _clean(raw: Any, limit: int = MAX_FACTS) -> List[Dict[str, str]]:
    out: List[Dict[str, str]] = []
    for item in (raw or {}).get("facts", []) if isinstance(raw, dict) else []:
        if not isinstance(item, dict) or item.get("topic") not in TOPICS:
            continue
        fact = " ".join(str(item.get("fact", "")).split())[:MAX_FACT_CHARS]
        if fact:
            out.append({"topic": item["topic"], "fact": fact})
    return out[:limit]


async def extract_and_propose(user_text: str, client: Optional[OllamaClient] = None) -> int:
    """Returns how many proposals were filed. Never raises."""
    start = time.monotonic()
    filed = 0
    error: Optional[str] = None
    try:
        text = own_words(user_text)
        if not settings.memory_extraction or len(text) < MIN_CHARS or _SECRET.search(text):
            return 0
        if _busy.locked():
            return 0  # skip, never queue: extraction must not pile onto the GPU
        async with _busy:
            client = client or OllamaClient(settings.ollama_url)
            reply = await client.chat_json(
                [{"role": "system", "content": SYSTEM_PROMPT}, {"role": "user", "content": text}], SCHEMA
            )
            by_topic: Dict[str, List[str]] = {}
            for item in _clean(reply):
                if not _already_known(item["fact"], item["topic"]):
                    by_topic.setdefault(item["topic"], []).append(item["fact"])
            for topic, facts in by_topic.items():
                bullets = "\n".join(f"- {f}" for f in facts)
                try:
                    proposals.create(
                        "memory", "append_to_file", {"path": f"Profile/{topic}.md", "text": bullets},
                        f"Remember: {facts[0][:60]}", bullets,
                    )
                    filed += 1
                except toolkit.ToolError as exc:
                    logger.info("Memory proposal refused: %s", exc)
    except Exception as exc:  # noqa: BLE001 - background work must never take anything down
        error = type(exc).__name__
        logger.debug("Memory extraction failed", exc_info=True)
    finally:
        if settings.memory_extraction:
            record(route="memory", model=LLM_MODEL, ms_total=int((time.monotonic() - start) * 1000),
                   proposals=filed, error=error)
    return filed


def schedule(user_text: str) -> None:
    """Fire and forget, from inside a request. A no-op when there is nothing worth sending."""
    if not settings.memory_extraction or len(own_words(user_text)) < MIN_CHARS:
        return
    try:
        task = asyncio.get_running_loop().create_task(extract_and_propose(user_text))
    except RuntimeError:
        return
    _tasks.add(task)  # a strong reference, or the task can be garbage collected mid-run
    task.add_done_callback(_tasks.discard)
