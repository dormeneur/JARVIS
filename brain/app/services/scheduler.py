"""A tiny scheduler for jv-brain: reindex, daily digest, trash prune, proposal expiry.

The same pattern as jv-api's backup loop. Jobs are a fixed list in code; the only state is each
job's last run time in <vault>/.system/brain/scheduler.json. A daily job that was missed (the PC
was off at 07:30) runs once on the next tick after the PC is back. A job that fails is logged and
counts as run, so a failing job can never hot-loop.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Awaitable, Callable, Dict, List, Optional, Tuple
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from app.config import settings

logger = logging.getLogger(__name__)

TICK_SECONDS = 60

# ("daily", "HH:MM") or ("interval", minutes)
Spec = Tuple[str, Any]


def local_tz():
    try:
        return ZoneInfo(settings.timezone)
    except (ZoneInfoNotFoundError, ValueError):
        logger.warning("Unknown JARVIS_TIMEZONE %r, using UTC", settings.timezone)
        return timezone.utc


def _at(day: datetime, hhmm: str) -> datetime:
    hour, minute = (int(x) for x in hhmm.split(":"))
    return day.replace(hour=hour, minute=minute, second=0, microsecond=0)


def is_due(now: datetime, last_run: Optional[datetime], spec: Spec) -> bool:
    """Pure: should a job with this spec run now? `now` and `last_run` are in the same timezone."""
    kind, value = spec
    if kind == "interval":
        return last_run is None or now - last_run >= timedelta(minutes=value)
    if kind == "daily":
        slot = _at(now, value)
        return now >= slot and (last_run is None or last_run < slot)
    raise ValueError(f"unknown schedule kind {kind!r}")


def state_path() -> Path:
    return Path(settings.vault_path) / ".system" / "brain" / "scheduler.json"


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
    os.replace(tmp, path)  # atomic: a crash never leaves half a file


# --- jobs ------------------------------------------------------------------------------------

async def _reindex(app) -> None:
    indexer = app.state.indexer
    if not indexer.is_indexing:
        indexer.start_background_indexing()


async def _digest(app) -> None:
    from app.services import digest

    await digest.run_digest(app)


async def _trash_prune(app) -> None:
    from app.services import tools

    await asyncio.to_thread(tools.prune_trash)


async def _proposal_expire(app) -> None:
    from app.services import proposals

    await asyncio.to_thread(proposals.expire_old)


def jobs() -> List[Tuple[str, Spec, Callable[[Any], Awaitable[None]]]]:
    return [
        ("reindex", ("interval", 30), _reindex),
        ("digest", ("daily", settings.digest_time), _digest),
        ("trash_prune", ("daily", "03:00"), _trash_prune),
        ("proposal_expire", ("daily", "03:05"), _proposal_expire),
    ]


async def run_due(app, now: Optional[datetime] = None) -> List[str]:
    """Run every job that is due. Returns the names that ran."""
    now = (now or datetime.now(local_tz())).astimezone(local_tz())
    state = load_state()
    ran: List[str] = []
    for name, spec, func in jobs():
        raw = state.get(name)
        last = datetime.fromisoformat(raw).astimezone(local_tz()) if raw else None
        try:
            if not is_due(now, last, spec):
                continue
        except ValueError:
            logger.error("Bad schedule for %s: %r", name, spec)
            continue
        try:
            await func(app)
            ran.append(name)
        except Exception:  # noqa: BLE001 - one job failing must not stop the others
            logger.exception("Scheduled job %s failed", name)
        state[name] = now.isoformat()
        save_state(state)
    return ran


async def scheduler_loop(app) -> None:
    while True:
        try:
            await run_due(app)
        except Exception:  # noqa: BLE001
            logger.exception("Scheduler tick failed")
        await asyncio.sleep(TICK_SECONDS)
