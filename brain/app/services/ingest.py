"""Bringing outside text into the vault, organised so it's found exactly when it's needed.

The user selects text anywhere (a web page, a document, a chat) and says "save to Jarvis". The local
model reads it once and proposes where it belongs; the desktop shows that plan and writes it only
when the user agrees. The rules keep it honest:

* The original text is the note: it is kept word for word (with its source), never rewritten, so
  whatever answers from it later (chat, forms) is answering from what was really there.
* The model only adds a title, a folder, a one-line summary and tags, and lifts out facts about the
  owner when the text is about them. A summary, tag or fact whose words aren't in the text is dropped
  (a small model's invention never reaches the vault).
* Facts about the owner go to the right Profile file (profile_place), where forms and hunches read.
* Never Secrets, never system folders, never Profile directly; secret-looking text is refused.
"""
from __future__ import annotations

import logging
import re
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional

from app.config import settings
from app.services import profile_place
from app.services.document_loader import EXCLUDED_FOLDERS
from app.services.memory_extractor import _SECRET
from app.services.ollama_client import OllamaClient

logger = logging.getLogger(__name__)

MAX_TEXT = 20000
DEFAULT_FOLDER = "Clippings"
# Profile is fed through facts only; the agent's and the digest's folders are theirs.
_OFF_LIMITS = {f.casefold() for f in EXCLUDED_FOLDERS} | {"system", ".system", "profile", "agent", "digest"}
_FOLDER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9 _-]{1,39}$")
_BAD_NAME = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_WORD = re.compile(r"[a-z0-9]{4,}")

SYSTEM_PROMPT = """You organise something the owner of a notes vault chose to keep. Use ONLY the text given.
- "title": a short descriptive title, at most 8 words.
- "folder": the existing folder it belongs in; only if none fits, a short new folder name.
- "summary": one sentence that only restates what the text says.
- "tags": 2 to 5 lowercase keywords that appear in the text.
- "about_me": facts about the owner themselves, ONLY if the text is written by them or is explicitly
  about them (their CV, a message they wrote about themselves). Use their words. Usually this is empty.
Never add anything the text does not say."""

SCHEMA = {
    "type": "object",
    "properties": {
        "title": {"type": "string"},
        "folder": {"type": "string"},
        "summary": {"type": "string"},
        "tags": {"type": "array", "items": {"type": "string"}},
        "about_me": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["title", "folder", "summary", "tags", "about_me"],
}


def _words(text: str) -> set:
    return set(_WORD.findall(text.casefold()))


def grounded(claim: str, text: str, share: float) -> bool:
    """Most of a claim's meaningful words are in the text (the model didn't make it up)."""
    words = _words(claim)
    return bool(words) and len(words & _words(text)) / len(words) >= share


def folders() -> List[str]:
    root = Path(settings.vault_path)
    if not root.is_dir():
        return []
    return sorted(p.name for p in root.iterdir() if p.is_dir() and not p.name.startswith(".") and p.name.casefold() not in _OFF_LIMITS)


def clean_folder(raw: str, existing: List[str]) -> str:
    name = " ".join(str(raw or "").replace("\\", "/").split("/")[0].split())
    for folder in existing:
        if folder.casefold() == name.casefold():
            return folder
    if not _FOLDER.match(name) or name.casefold() in _OFF_LIMITS:
        return DEFAULT_FOLDER
    return name


def clean_title(raw: str, text: str) -> str:
    title = " ".join(_BAD_NAME.sub(" ", str(raw or "")).split()).strip(". ")[:60]
    if len(title) < 3:
        title = " ".join(_BAD_NAME.sub(" ", text).split()[:8])[:60].strip(". ") or "Clipping"
    return title


def _free_path(folder: str, title: str) -> str:
    root = Path(settings.vault_path)
    path, n = f"{folder}/{title}.md", 2
    while (root / path).exists():
        path, n = f"{folder}/{title} ({n}).md", n + 1
    return path


def note_text(title: str, text: str, summary: str, tags: List[str], source: str, url: Optional[str]) -> str:
    when = datetime.now().astimezone().strftime("%Y-%m-%d")
    front = ["---", f"captured: {when}"]
    if source:
        front.append(f"source: {source}")
    if url:
        front.append(f"url: {url}")
    if tags:
        front.append("tags: [" + ", ".join(tags) + "]")
    front.append("---")
    body = [*front, "", f"# {title}", ""]
    if summary:
        body += [f"> {summary}", ""]
    body.append(text.strip())
    return "\n".join(body) + "\n"


async def plan(text: str, source: str = "", url: Optional[str] = None, client: Optional[OllamaClient] = None) -> Dict[str, Any]:
    """What saving this would write: a note (its path and content) and facts for Profile. Nothing is written here."""
    text = (text or "").strip()[:MAX_TEXT]
    if len(text) < 3:
        raise ValueError("nothing to save")
    if _SECRET.search(text):
        raise ValueError("that looks like a password or a number to keep in Secrets, not in your notes")
    existing = folders()
    prompt = ("Existing folders: " + (", ".join(existing) or "(none yet)")
              + (f"\nIt came from: {source}" if source else "") + f"\n\nTEXT:\n{text[:6000]}")
    client = client or OllamaClient(settings.ollama_url)
    try:
        reply = await client.chat_json(
            [{"role": "system", "content": SYSTEM_PROMPT}, {"role": "user", "content": prompt}], SCHEMA, timeout=120
        ) or {}
    except Exception as exc:  # noqa: BLE001 - the text is still worth keeping, just less organised
        logger.warning("Ingest fell back to plain filing: the local model failed (%s)", type(exc).__name__)
        reply = {}

    title = clean_title(reply.get("title", ""), text)
    folder = clean_folder(reply.get("folder", ""), existing)
    summary = " ".join(str(reply.get("summary", "")).split())[:300]
    summary = summary if grounded(summary, text, 0.6) else ""
    tags = [t for t in dict.fromkeys(str(t).strip().casefold() for t in reply.get("tags") or []) if t and t in text.casefold()][:5]

    facts: List[Dict[str, str]] = []
    for fact in (reply.get("about_me") or [])[:5]:
        fact = " ".join(str(fact).split())[:300]
        if not fact or not grounded(fact, text, 0.7):
            continue
        try:
            facts.append({"path": await profile_place.place(fact, client), "fact": fact})
        except ValueError:
            continue

    path = _free_path(folder, title)
    return {"note": {"path": path, "content": note_text(title, text, summary, tags, source, url)}, "facts": facts}
