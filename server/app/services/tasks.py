"""Tasks across the vault: every "- [ ]" / "- [x]" line in any note, for the phone's Tasks screen.

Reading walks the vault (never Secrets, system or hidden folders). Ticking a task off rewrites
exactly that one line through vault.update_file, so the change is versioned and every device syncs
it; if the line changed elsewhere meanwhile, nothing is written and the caller reloads. New tasks go
to Agent/tasks.md, the same inbox the desktop's "add a task" and the vault agent use.
"""
from __future__ import annotations

import os
import re
from datetime import date
from pathlib import Path
from typing import Dict, List, Optional

from app import config
from app.services import vault

INBOX = "Agent/tasks.md"
MAX_TASK = 300
MAX_OPEN = 300
MAX_DONE = 40
_SKIP = {"secrets", "system", ".system", ".git", ".obsidian", "trash"}
_TASK = re.compile(r"^(\s*[-*]\s+\[)([ xX])(\]\s+)(.+?)\s*$")


def _files(root: Path) -> List[Path]:
    found = []
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d.casefold() not in _SKIP and not d.startswith(".")]
        found += [Path(folder, n) for n in names if n.lower().endswith(".md")]
    return found


def scan(root: Optional[Path] = None) -> Dict[str, List[Dict]]:
    """Open tasks (the inbox first, then the most recently changed notes) and recently done ones."""
    root = Path(root or config.settings.vault_path)
    files = sorted(_files(root), key=lambda p: (p.relative_to(root).as_posix() != INBOX, -p.stat().st_mtime))
    open_tasks: List[Dict] = []
    done: List[Dict] = []
    for p in files:
        rel = p.relative_to(root).as_posix()
        try:
            lines = p.read_text(encoding="utf-8", errors="replace").splitlines()
        except OSError:
            continue
        for number, line in enumerate(lines, 1):
            m = _TASK.match(line)
            if not m:
                continue
            item = {"path": rel, "line": number, "text": m.group(4), "done": m.group(2) != " "}
            (done if item["done"] else open_tasks).append(item)
    return {"open": open_tasks[:MAX_OPEN], "done": done[:MAX_DONE]}


def toggle(rel: str, text: str, done: bool) -> bool:
    """Ticks (or unticks) the first task in `rel` whose text is `text`. False: not there any more."""
    target = vault.get_path(rel)
    content = getattr(target, "content", None)
    if content is None:
        return False
    lines = content.replace("\r\n", "\n").split("\n")  # a note saved on Windows must not gain blank lines
    for i, line in enumerate(lines):
        m = _TASK.match(line)
        if m and m.group(4) == text.strip() and (m.group(2) != " ") != done:
            lines[i] = f"{m.group(1)}{'x' if done else ' '}{m.group(3)}{m.group(4)}"
            vault.update_file(rel, "\n".join(lines))
            return True
    return False


def add(text: str, today: Optional[date] = None) -> str:
    """Appends a task to the inbox (created with a heading the first time). Returns the line written."""
    text = " ".join((text or "").split())[:MAX_TASK]
    if not text:
        raise ValueError("a task needs words")
    line = f"- [ ] {text}  (added {(today or date.today()).isoformat()})"
    root = Path(config.settings.vault_path)
    if (root / INBOX).is_file():
        old = (root / INBOX).read_text(encoding="utf-8", errors="replace")
        vault.update_file(INBOX, (old if old.endswith("\n") else old + "\n") + line + "\n")
    else:
        vault.create_file(INBOX, content=f"# Tasks\n\n{line}\n")
    return line
