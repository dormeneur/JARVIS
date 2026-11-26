"""Jobs sent from the phone to the PC's Jarvis (the desktop app runs them as background jobs).

The phone adds a job; the desktop app claims the oldest queued one, runs it, and reports back; the
phone reads the status. No push service: both sides ask jv-api, which keeps the queue in
<vault>/system/relay-jobs.json (system/ is unreachable through the files API, like the device
registry). A job's brief goes to the desktop's agent (Claude), so it must not carry secrets: briefs
that look like passwords or keys are refused here, before they're queued.
"""
from __future__ import annotations

import json
import os
import re
import threading
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Dict, List, Optional

from app import config

QUEUED, RUNNING, FINISHED, FAILED = "queued", "running", "finished", "failed"
KEPT = 30
MAX_BRIEF = 2000
MAX_RESULT = 4000
STALE_RUNNING = timedelta(hours=6)
_SECRET = re.compile(
    r"(password|passcode|passwd|\bpin\b|\botp\b)\s*[:=]|sk-[A-Za-z0-9]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b\d{12,19}\b",
    re.IGNORECASE,
)
_lock = threading.Lock()


def _path() -> Path:
    folder = Path(config.settings.vault_path) / "system"
    folder.mkdir(parents=True, exist_ok=True)
    return folder / "relay-jobs.json"


def _now() -> datetime:
    return datetime.now(tz=timezone.utc)


def _load() -> List[Dict]:
    try:
        jobs = json.loads(_path().read_text(encoding="utf-8"))
        return jobs if isinstance(jobs, list) else []
    except (OSError, ValueError):
        return []


def _save(jobs: List[Dict]) -> None:
    path = _path()
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(jobs[-KEPT:], indent=1), encoding="utf-8")
    os.replace(tmp, path)  # atomic


def _title(brief: str) -> str:
    words = brief.split()
    return " ".join(words[:6]).rstrip(".,:;") + ("…" if len(words) > 6 else "")


def _tidy(jobs: List[Dict]) -> List[Dict]:
    """A job left running when the PC went away is failed after a while, so it never spins forever."""
    for job in jobs:
        if job["status"] == RUNNING and _now() - datetime.fromisoformat(job["updated"]) > STALE_RUNNING:
            job.update(status=FAILED, result="The PC stopped before it finished.", updated=_now().isoformat())
    return jobs


def create(brief: str, device: str) -> Dict:
    brief = (brief or "").strip()[:MAX_BRIEF]
    if len(brief) < 3:
        raise ValueError("tell the PC what to do")
    if _SECRET.search(brief):
        raise ValueError("that looks like it has a password or key in it; jobs run on Claude, so it wasn't sent")
    job = {"id": uuid.uuid4().hex[:12], "brief": brief, "title": _title(brief), "status": QUEUED,
           "created": _now().isoformat(), "updated": _now().isoformat(), "from": device, "result": None}
    with _lock:
        jobs = _tidy(_load())
        jobs.append(job)
        _save(jobs)
    return job


def recent() -> List[Dict]:
    """Newest first."""
    with _lock:
        jobs = _tidy(_load())
        _save(jobs)
    return list(reversed(jobs))


def claim(device: str) -> Optional[Dict]:
    """The oldest queued job, now marked running by `device`; None when there's nothing to do."""
    with _lock:
        jobs = _tidy(_load())
        job = next((j for j in jobs if j["status"] == QUEUED), None)
        if job is not None:
            job.update(status=RUNNING, updated=_now().isoformat(), by=device)
        _save(jobs)
    return job


def report(job_id: str, status: str, result: Optional[str]) -> Optional[Dict]:
    if status not in (RUNNING, FINISHED, FAILED):
        raise ValueError("status must be running, finished or failed")
    with _lock:
        jobs = _load()
        job = next((j for j in jobs if j["id"] == job_id), None)
        if job is not None:
            job.update(status=status, updated=_now().isoformat())
            if result is not None:
                job["result"] = result[:MAX_RESULT]
            _save(jobs)
    return job
