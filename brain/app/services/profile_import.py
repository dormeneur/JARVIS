"""Turn the documents you keep about yourself into proposed Profile facts.

Run on demand, on this PC, with the local model only:
    docker exec jv-brain python -m app.services.profile_import              # new/changed files in Personal/
    docker exec jv-brain python -m app.services.profile_import --dry-run    # counts only, files nothing
    docker exec jv-brain python -m app.services.profile_import --folder Work --force

For each document the model lists durable facts about *you* (identity, work, education, skills,
goals, preferences, people). They are grouped per topic and filed as proposals to append to
Profile/<topic>.md; nothing is written until you approve them in the app's Inbox. Documents are
remembered by hash, so a re-run only reads what is new or changed. Facts that look like secrets or
ID numbers are dropped, and facts already in the Profile or the Inbox are not proposed again.
Only counts are printed: the facts themselves are shown in the Inbox, never in logs.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import re
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from app.config import settings
from app.services import proposals
from app.services import tools as toolkit
from app.services.document_loader import DocumentLoader
from app.services.memory_extractor import SCHEMA, TOPICS, _already_known, _clean, _norm, _SECRET
from app.services.ollama_client import LLM_MODEL, OllamaClient
from app.services.traces import record

logger = logging.getLogger(__name__)

WINDOW_CHARS = 4000        # ~1k tokens: a dense window can still be answered inside the reply limit
MAX_WINDOWS = 5            # a document longer than 20k characters is read from the top only
FACTS_PER_WINDOW = 10
BULLETS_PER_PROPOSAL = 15

SYSTEM_PROMPT = (
    "The text below is a document that the user wrote or keeps about themselves. List durable facts "
    "about THE USER (the person it is about): identity, work, education, skills, goals, preferences, "
    "important people. One short fact per item, in the document's own words. Skip facts about other "
    "people or companies, generic advice, to-do items and anything that will change soon. Never "
    "include passwords, ID, passport or account numbers. If there is nothing, return an empty list."
)

# Beyond the shared secret pattern: government/bank identifiers followed by digits.
_ID_NUMBER = re.compile(r"\b(passport|aadhaar|aadhar|ssn|iban|cvv|ifsc|pan)\b.{0,30}\d{4,}", re.IGNORECASE)


def state_path() -> Path:
    return Path(settings.vault_path) / ".system" / "brain" / "profile_import.json"


def load_state() -> Dict[str, str]:
    try:
        return json.loads(state_path().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_state(state: Dict[str, str]) -> None:
    path = state_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(state), encoding="utf-8")
    os.replace(tmp, path)


def windows(text: str) -> List[str]:
    return [text[i : i + WINDOW_CHARS] for i in range(0, min(len(text), WINDOW_CHARS * MAX_WINDOWS), WINDOW_CHARS)]


def safe_fact(fact: str) -> bool:
    return not (_SECRET.search(fact) or _ID_NUMBER.search(fact))


def group(facts: List[Dict[str, str]]) -> Dict[str, List[str]]:
    """Per topic, in order, without repeats (compared ignoring case and spacing)."""
    by_topic: Dict[str, List[str]] = {}
    seen: set = set()
    for item in facts:
        key = (item["topic"], _norm(item["fact"]))
        if key in seen or not safe_fact(item["fact"]):
            continue
        seen.add(key)
        by_topic.setdefault(item["topic"], []).append(item["fact"])
    return by_topic


async def extract_document(client: OllamaClient, text: str) -> Tuple[List[Dict[str, str]], bool]:
    """(facts, complete). One window failing (e.g. the model's JSON was cut off) loses only that
    window; `complete` is False so the file is read again next time."""
    facts: List[Dict[str, str]] = []
    complete = True
    for part in windows(text):
        try:
            reply = await client.chat_json(
                [{"role": "system", "content": SYSTEM_PROMPT}, {"role": "user", "content": part}], SCHEMA
            )
        except Exception as exc:  # noqa: BLE001
            logger.warning("Profile import: a window failed (%s)", type(exc).__name__)
            complete = False
            continue
        facts += _clean(reply, FACTS_PER_WINDOW)
    return facts, complete


async def run(folder: str = "Personal", *, force: bool = False, dry_run: bool = False,
              client: Optional[OllamaClient] = None) -> Dict[str, Any]:
    """Returns counts: files read, facts found, facts new, proposals filed (per topic in 'topics')."""
    start = time.monotonic()
    client = client or OllamaClient(settings.ollama_url)
    state = {} if force else load_state()
    prefix = folder.strip("/").casefold() + "/"
    read = skipped = 0
    found: List[Dict[str, str]] = []

    for doc in DocumentLoader(str(settings.vault_path)).load_documents():
        rel = doc.path.replace(chr(92), "/")
        if not rel.casefold().startswith(prefix):
            continue
        if state.get(rel) == doc.content_hash:
            skipped += 1
            continue
        facts, complete = await extract_document(client, doc.content)
        found += facts
        read += 1
        if complete:
            state[rel] = doc.content_hash
            if not dry_run:
                save_state(state)  # progress survives an interruption; unfinished files are read again

    by_topic = {
        topic: [f for f in facts if not _already_known(f, topic)] for topic, facts in group(found).items()
    }
    by_topic = {t: f for t, f in by_topic.items() if f}
    filed = 0
    if not dry_run:
        for topic, facts in by_topic.items():
            for n, i in enumerate(range(0, len(facts), BULLETS_PER_PROPOSAL), start=1):
                chunk = facts[i : i + BULLETS_PER_PROPOSAL]
                bullets = "\n".join(f"- {f}" for f in chunk)
                part = f" (part {n})" if len(facts) > BULLETS_PER_PROPOSAL else ""
                try:
                    proposals.create(
                        "documents", "append_to_file", {"path": f"Profile/{topic}.md", "text": bullets},
                        f"From your documents: {topic}{part}, {len(chunk)} facts", bullets,
                    )
                    filed += 1
                except toolkit.ToolError as exc:
                    logger.info("Profile proposal refused: %s", exc)

    result = {
        "files_read": read, "files_unchanged": skipped, "facts_found": len(found),
        "facts_new": sum(len(v) for v in by_topic.values()), "proposals_filed": filed,
        "topics": {t: len(f) for t, f in by_topic.items()},
    }
    record(route="profile_import", model=LLM_MODEL, ms_total=int((time.monotonic() - start) * 1000),
           **{k: v for k, v in result.items() if k != "topics"})
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--folder", default="Personal", help="vault folder to read (default: Personal)")
    parser.add_argument("--force", action="store_true", help="read every file again, not just new/changed ones")
    parser.add_argument("--dry-run", action="store_true", help="count only: file nothing, remember nothing")
    args = parser.parse_args()
    result = asyncio.run(run(args.folder, force=args.force, dry_run=args.dry_run))
    print(json.dumps(result, indent=2))
    if result["proposals_filed"]:
        print("Review them in the JARVIS app: Inbox.")


if __name__ == "__main__":
    main()
