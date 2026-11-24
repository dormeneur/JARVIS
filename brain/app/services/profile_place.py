"""Where a new fact about the user belongs: an existing Profile file, or a new topic (or folder).

The desktop's "About me" lets the user type something about themselves; the local model picks the
file it belongs in from the Profile files that exist, or names a new one. The answer is held to a
strict shape (Profile/<topic>.md or Profile/<folder>/<topic>.md, lowercase slugs) so a model can never
choose a path outside Profile, a hidden file, or another feature's folder. Secret-looking text is
refused before the model sees it.
"""
from __future__ import annotations

import logging
import os
import re
from pathlib import Path
from typing import List, Optional

from app.config import settings
from app.services.memory_extractor import _SECRET
from app.services.ollama_client import OllamaClient

FALLBACK = "Profile/about.md"
logger = logging.getLogger(__name__)
SKIP_FILES = {"hunches.md", "not-me.md"}           # the brain's own view, and wrong guesses
SKIP_FOLDERS = {"forms", "resume", "documents"}     # other features' folders
_VALID = re.compile(r"^Profile/(?:[a-z0-9][a-z0-9-]{0,39}/)?[a-z0-9][a-z0-9-]{0,39}\.md$")

SYSTEM_PROMPT = """You file one fact about a person into their notes.
Pick the existing file it belongs in. Only if none fits, name a new file: "Profile/<topic>.md", or
"Profile/<folder>/<topic>.md" for something that will grow its own group (for example
"Profile/people/family.md"). Topics are short lowercase words joined by hyphens.
Reply with the path only."""

SCHEMA = {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}


def existing_files() -> List[str]:
    root = Path(settings.vault_path) / "Profile"
    found: List[str] = []
    if not root.is_dir():
        return found
    for folder, dirs, names in os.walk(root):
        rel = Path(folder).relative_to(root)
        if rel.parts and (len(rel.parts) > 1 or rel.parts[0].casefold() in SKIP_FOLDERS or rel.parts[0].startswith(".")):
            dirs[:] = []
            continue
        dirs[:] = [d for d in dirs if d.casefold() not in SKIP_FOLDERS and not d.startswith(".")]
        found += [f"Profile/{(rel / n).as_posix()}" for n in names if n.endswith(".md") and n not in SKIP_FILES]
    return sorted(found)


def clean(raw: str, files: List[str]) -> str:
    """The model's answer held to a safe Profile path; the fallback when it isn't one."""
    path = (raw or "").strip().strip("`'\" ").replace("\\", "/")
    if path in files:
        return path
    path = path.lower()
    if not path.startswith("Profile/".lower()):
        path = "profile/" + path.lstrip("/")
    path = "Profile/" + path[len("profile/"):]
    if not path.endswith(".md"):
        path += ".md"
    parts = path.split("/")
    if not _VALID.match(path) or parts[-1] in SKIP_FILES or (len(parts) == 3 and parts[1] in SKIP_FOLDERS):
        return FALLBACK
    return path


async def place(fact: str, client: Optional[OllamaClient] = None) -> str:
    """The path a fact belongs in. Raises ValueError for secret-looking text (it isn't filed at all)."""
    fact = " ".join((fact or "").split())[:500]
    if not fact:
        raise ValueError("empty fact")
    if _SECRET.search(fact):
        raise ValueError("that looks like a password or a number to keep in Secrets, not your profile")
    files = existing_files()
    prompt = "Existing files:\n" + ("\n".join(f"- {f}" for f in files) or "(none yet)") + f"\n\nFact: {fact}"
    client = client or OllamaClient(settings.ollama_url)
    try:
        reply = await client.chat_json(
            [{"role": "system", "content": SYSTEM_PROMPT}, {"role": "user", "content": prompt}], SCHEMA, timeout=60
        )
    except Exception as exc:  # noqa: BLE001 - the model being down still files the fact somewhere sensible
        logger.warning("Placement fell back to %s: the local model failed (%s)", FALLBACK, type(exc).__name__)
        return FALLBACK if FALLBACK in files or not files else files[0]
    return clean(str((reply or {}).get("path", "")), files)
