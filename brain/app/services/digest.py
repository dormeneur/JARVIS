"""Daily digest: a short Markdown summary written into the vault as Digest/YYYY-MM-DD.md.

Written by the local model from facts it is handed (what changed in the last 24 h, what is waiting
in the Inbox) and told to mention nothing else. Being a vault file it syncs to the phone and is
searchable like any note. The optional push says only that a digest exists, never what is in it.
"""
from __future__ import annotations

import logging
import time
from datetime import datetime, timedelta
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import httpx

from app.config import settings
from app.services import proposals, tools as toolkit
from app.services.document_loader import DocumentLoader, EXCLUDED_FOLDERS
from app.services.ollama_client import OllamaClient
from app.services.scheduler import local_tz

logger = logging.getLogger(__name__)

WINDOW = timedelta(hours=24)
MAX_FILES = 20
# The digest must not summarise itself, the generated index, or the per-form answer archive.
SKIP_TOP = frozenset({"digest", "memory"} | {f.casefold() for f in EXCLUDED_FOLDERS})
SKIP_PREFIXES = ("profile/forms/",)

PROMPT = (
    "Write today's digest in Markdown, under 200 words, with the sections '## Changed yesterday' and "
    "'## Waiting for you'. Mention only the items listed below. Do not invent anything, and say "
    "'Nothing' for a section that has no items.\n\n"
)


def _skipped(rel: str) -> bool:
    low = rel.replace("\\", "/").casefold()
    return low.split("/")[0] in SKIP_TOP or low.startswith(SKIP_PREFIXES)


def recent_files(now: datetime) -> List[Tuple[str, float]]:
    """(vault-relative path, mtime) of files changed in the last 24 h, newest first."""
    root = Path(settings.vault_path)
    cutoff = (now - WINDOW).timestamp()
    found: List[Tuple[str, float]] = []
    for path in DocumentLoader(str(root))._walk_vault():
        rel = str(path.relative_to(root)).replace("\\", "/")
        if _skipped(rel):
            continue
        try:
            mtime = path.stat().st_mtime
        except OSError:
            continue
        if mtime >= cutoff:
            found.append((rel, mtime))
    found.sort(key=lambda x: x[1], reverse=True)
    return found[:MAX_FILES]


async def _summaries(app) -> Dict[str, str]:
    try:
        metas = await app.state.vector_store.get_all_metadata()
    except Exception:  # noqa: BLE001 - summaries are a nicety
        return {}
    return {m["source_path"]: m.get("summary", "") for m in metas if m.get("source_path")}


async def _push() -> None:
    if not settings.ntfy_url:
        return
    try:
        async with httpx.AsyncClient(timeout=5) as client:
            await client.post(settings.ntfy_url, content="Your daily digest is ready", headers={"Title": "JARVIS"})
    except Exception:  # noqa: BLE001
        logger.info("Digest push failed (ignored)")


async def run_digest(app, *, force: bool = False, now: Optional[datetime] = None) -> Optional[str]:
    """Write today's digest. Returns its vault path, or None if there was nothing to say or it exists."""
    now = (now or datetime.now(local_tz())).astimezone(local_tz())
    rel = f"Digest/{now.date().isoformat()}.md"
    target = toolkit.safe_path(rel)
    if target.exists() and not force:
        return None

    files = recent_files(now)
    pending = proposals.list_proposals(limit=5)
    if not files and not pending:
        logger.info("Quiet day, no digest")
        return None

    summaries = await _summaries(app)
    lines = ["Changed in the last 24 hours:"] + [
        f"- {path}" + (f": {summaries[path]}" if summaries.get(path) else "") for path, _ in files
    ] if files else ["Changed in the last 24 hours: nothing"]
    lines += [f"Waiting in the Inbox ({proposals.count_pending()}):"] + (
        [f"- {p['title']}" for p in pending] if pending else ["- nothing"]
    )

    start = time.monotonic()
    text, _ = await OllamaClient(settings.ollama_url).generate(PROMPT + "\n".join(lines))
    text = text.strip()
    if not text:
        return None
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(f"# Digest {now.date().isoformat()}\n\n{text}\n".encode("utf-8"))
    logger.info("Digest written to %s in %.1fs", rel, time.monotonic() - start)
    await _push()
    return rel
