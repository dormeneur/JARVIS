"""Proposal queue: things JARVIS wants to do that wait for the user's yes.

Background features (learning facts from chat, later mail drafts) can't ask in a live chat, so they
file a proposal here; the phone's Inbox shows it and the user approves or rejects. Approving runs
the same tool the chat agent would, through the same safe_path guard.

Rules, all enforced here and not by callers:
  * only mutating, non-destructive tools: read tools make no sense and deletions are never proposed
  * every path is validated when the proposal is created (a Secrets path fails then, not later)
  * approving a proposal whose target file changed since it was filed is skipped as stale
  * a decision is idempotent (a double tap is harmless) and runs under a lock, so two devices
    approving the same proposal run it once
State: <vault>/.system/brain/proposals.db (journal_mode=DELETE, see keyword_index.py).
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import sqlite3
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional

from app.config import settings
from app.services import tools as toolkit

EXPIRE_DAYS = 14
PATH_KEYS = ("path", "source", "destination")

PENDING, DONE, FAILED, REJECTED, STALE, EXPIRED = "pending", "done", "failed", "rejected", "stale", "expired"

_lock = asyncio.Lock()


def _db_path() -> Path:
    return Path(settings.vault_path) / ".system" / "brain" / "proposals.db"


def _connect() -> sqlite3.Connection:
    path = _db_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(path, timeout=30)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=DELETE")
    conn.execute(
        "CREATE TABLE IF NOT EXISTS proposals ("
        "id TEXT PRIMARY KEY, created_at TEXT, source TEXT, tool TEXT, arguments TEXT, title TEXT, "
        "detail TEXT, base_hash TEXT, status TEXT, decided_at TEXT, result TEXT)"
    )
    return conn


def _now() -> datetime:
    return datetime.now(tz=timezone.utc)


def _row(r: sqlite3.Row) -> Dict[str, Any]:
    d = dict(r)
    d["arguments"] = json.loads(d["arguments"])
    return d


def _canonical(args: Dict[str, Any]) -> str:
    return json.dumps(args, sort_keys=True, ensure_ascii=False)


def _file_hash(args: Dict[str, Any]) -> Optional[str]:
    """Fingerprint of the file this proposal will change; None when it can't go stale."""
    raw = args.get("path") or args.get("source")
    if not raw:
        return None
    target = toolkit.safe_path(raw)
    return hashlib.sha256(target.read_bytes()).hexdigest() if target.is_file() else "absent"


def create(source: str, tool: str, arguments: Dict[str, Any], title: str, detail: str) -> Dict[str, Any]:
    """File a proposal. Raises toolkit.ToolError if the tool or a path is not allowed."""
    spec = toolkit.TOOLS.get(tool)
    if spec is None:
        raise toolkit.ToolError(f"unknown tool '{tool}'")
    if spec.risk != toolkit.RISK_WRITE:
        raise toolkit.ToolError(f"'{tool}' cannot be proposed (only plain write tools can)")
    for key in PATH_KEYS:
        if arguments.get(key):
            toolkit.safe_path(arguments[key])

    canonical = _canonical(arguments)
    with _connect() as conn:
        for existing in conn.execute("SELECT * FROM proposals WHERE status = ? AND tool = ?", (PENDING, tool)):
            if _canonical(json.loads(existing["arguments"])) == canonical:
                return _row(existing)
        pid = uuid.uuid4().hex
        base = None if tool == "append_to_file" else _file_hash(arguments)
        conn.execute(
            "INSERT INTO proposals(id, created_at, source, tool, arguments, title, detail, base_hash, status) "
            "VALUES (?,?,?,?,?,?,?,?,?)",
            (pid, _now().isoformat(), source, tool, canonical, title[:200], detail, base, PENDING),
        )
        return _row(conn.execute("SELECT * FROM proposals WHERE id = ?", (pid,)).fetchone())


def list_proposals(status: str = PENDING, limit: int = 50) -> List[Dict[str, Any]]:
    with _connect() as conn:
        rows = conn.execute(
            "SELECT * FROM proposals WHERE status = ? ORDER BY created_at DESC LIMIT ?", (status, limit)
        ).fetchall()
    return [_row(r) for r in rows]


def count_pending() -> int:
    with _connect() as conn:
        return conn.execute("SELECT count(*) FROM proposals WHERE status = ?", (PENDING,)).fetchone()[0]


def get(pid: str) -> Optional[Dict[str, Any]]:
    with _connect() as conn:
        r = conn.execute("SELECT * FROM proposals WHERE id = ?", (pid,)).fetchone()
    return _row(r) if r else None


def _finish(pid: str, status: str, result: str = "") -> Dict[str, Any]:
    with _connect() as conn:
        conn.execute(
            "UPDATE proposals SET status = ?, decided_at = ?, result = ? WHERE id = ?",
            (status, _now().isoformat(), result[:2000], pid),
        )
    return get(pid)  # type: ignore[return-value]


def expire_old(days: int = EXPIRE_DAYS) -> int:
    cutoff = (_now() - timedelta(days=days)).isoformat()
    with _connect() as conn:
        cur = conn.execute(
            "UPDATE proposals SET status = ?, decided_at = ? WHERE status = ? AND created_at < ?",
            (EXPIRED, _now().isoformat(), PENDING, cutoff),
        )
        return cur.rowcount


async def decide(pid: str, approve: bool) -> Dict[str, Any]:
    """Apply the user's decision. Raises KeyError for an unknown id. Idempotent."""
    async with _lock:
        proposal = get(pid)
        if proposal is None:
            raise KeyError(pid)
        if proposal["status"] != PENDING:
            return proposal
        if datetime.fromisoformat(proposal["created_at"]) < _now() - timedelta(days=EXPIRE_DAYS):
            return _finish(pid, EXPIRED, "Too old to apply")
        if not approve:
            return _finish(pid, REJECTED)

        args = proposal["arguments"]
        if proposal["base_hash"] is not None:
            try:
                current = _file_hash(args)
            except toolkit.ToolError as exc:
                return _finish(pid, FAILED, str(exc))
            if current != proposal["base_hash"]:
                return _finish(pid, STALE, "The file changed since this was proposed, so it was skipped")
        try:
            result = await toolkit.execute(proposal["tool"], args, None)
        except toolkit.ToolError as exc:
            return _finish(pid, FAILED, str(exc))
        return _finish(pid, DONE, result)
